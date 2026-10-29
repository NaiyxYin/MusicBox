// Read-only exported-framework metadata validation: honors assembly scope and type forwarders.
// Checks types and direct-method name/arity/parameter-count shapes, not full IL/runtime behavior.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
if (args.Length != 2) { Console.Error.WriteLine("Usage: RuntimeCheck <ModAssembly.dll> <exported-runtime-directory>"); return 2; }
var opened = new List<IDisposable>();
try
{
    var images = new Dictionary<string, MetadataReader>(StringComparer.OrdinalIgnoreCase);
    var definitions = new Dictionary<string, Dictionary<string, TypeDefinitionHandle>>(StringComparer.OrdinalIgnoreCase);
    string DefName(MetadataReader r, TypeDefinitionHandle h)
    { var t = r.GetTypeDefinition(h); return t.IsNested ? DefName(r, t.GetDeclaringType()) + "/" + r.GetString(t.Name) : r.GetString(t.Namespace) + "." + r.GetString(t.Name); }
    string RefName(MetadataReader r, TypeReferenceHandle h)
    { var t = r.GetTypeReference(h); return t.ResolutionScope.Kind == HandleKind.TypeReference ? RefName(r, (TypeReferenceHandle)t.ResolutionScope) + "/" + r.GetString(t.Name) : r.GetString(t.Namespace) + "." + r.GetString(t.Name); }
    string RefAssembly(MetadataReader r, TypeReferenceHandle h)
    {
        var t = r.GetTypeReference(h);
        return t.ResolutionScope.Kind == HandleKind.TypeReference ? RefAssembly(r, (TypeReferenceHandle)t.ResolutionScope)
            : t.ResolutionScope.Kind == HandleKind.AssemblyReference ? r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name) : "";
    }
    bool Framework(string assembly) => assembly == "mscorlib" || assembly == "netstandard" || assembly == "System" || assembly.StartsWith("System.", StringComparison.Ordinal);
    MetadataReader Open(string path)
    { var stream = File.OpenRead(path); opened.Add(stream); var pe = new PEReader(stream); opened.Add(pe); return pe.GetMetadataReader(); }
    foreach (string path in Directory.EnumerateFiles(args[1], "*.dll"))
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        if (!Framework(stem)) continue;
        var stream = File.OpenRead(path); opened.Add(stream); var pe = new PEReader(stream); opened.Add(pe);
        if (!pe.HasMetadata) continue;
        var reader = pe.GetMetadataReader(); string assembly = reader.GetString(reader.GetAssemblyDefinition().Name);
        images[assembly] = reader;
        var names = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        foreach (var h in reader.TypeDefinitions) names.TryAdd(DefName(reader, h), h);
        definitions[assembly] = names;
    }
    bool Resolve(string assembly, string name, HashSet<string> visited, out (MetadataReader Reader, TypeDefinitionHandle Handle) found)
    {
        found = default;
        if (!visited.Add(assembly + "|" + name) || !images.TryGetValue(assembly, out var reader)) return false;
        if (definitions[assembly].TryGetValue(name, out var definition)) { found = (reader, definition); return true; }
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            if (!Resolve(assembly, name[..slash], visited, out var parent)) return false;
            foreach (var h in parent.Reader.GetTypeDefinition(parent.Handle).GetNestedTypes())
                if (parent.Reader.GetString(parent.Reader.GetTypeDefinition(h).Name) == name[(slash + 1)..]) { found = (parent.Reader, h); return true; }
            return false;
        }
        foreach (var h in reader.ExportedTypes)
        {
            var type = reader.GetExportedType(h);
            if (reader.GetString(type.Namespace) + "." + reader.GetString(type.Name) != name) continue;
            if (type.Implementation.Kind != HandleKind.AssemblyReference) return false;
            string next = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.Implementation).Name);
            return Resolve(next, name, visited, out found);
        }
        return false;
    }
    bool ResolveRef(MetadataReader r, TypeReferenceHandle h, out (MetadataReader Reader, TypeDefinitionHandle Handle) found) =>
        Resolve(RefAssembly(r, h), RefName(r, h), new HashSet<string>(StringComparer.Ordinal), out found);
    var mod = Open(args[0]); var missing = new SortedSet<string>(StringComparer.Ordinal); int checkedTypes = 0, checkedMethods = 0;
    foreach (var h in mod.TypeReferences)
    {
        string assembly = RefAssembly(mod, h); if (!Framework(assembly)) continue; checkedTypes++;
        if (!ResolveRef(mod, h, out _)) missing.Add("TYPE [" + assembly + "] " + RefName(mod, h));
    }
    string Shape(MetadataReader r, BlobHandle signature, string name)
    { var b = r.GetBlobReader(signature); var h = b.ReadSignatureHeader(); int arity = h.IsGeneric ? b.ReadCompressedInteger() : 0; return name + "/generic=" + arity + "/args=" + b.ReadCompressedInteger(); }
    bool HasMethod((MetadataReader Reader, TypeDefinitionHandle Handle) entry, string shape, HashSet<string> visited)
    {
        string identity = entry.Reader.GetString(entry.Reader.GetAssemblyDefinition().Name) + "|" + DefName(entry.Reader, entry.Handle);
        if (!visited.Add(identity)) return false; var type = entry.Reader.GetTypeDefinition(entry.Handle);
        foreach (var h in type.GetMethods())
        { var m = entry.Reader.GetMethodDefinition(h); if (Shape(entry.Reader, m.Signature, entry.Reader.GetString(m.Name)) == shape) return true; }
        if (type.BaseType.Kind == HandleKind.TypeReference && ResolveRef(entry.Reader, (TypeReferenceHandle)type.BaseType, out var parent)) return HasMethod(parent, shape, visited);
        if (type.BaseType.Kind == HandleKind.TypeDefinition) return HasMethod((entry.Reader, (TypeDefinitionHandle)type.BaseType), shape, visited);
        return false;
    }
    foreach (var h in mod.MemberReferences)
    {
        var m = mod.GetMemberReference(h);
        if (m.GetKind() != MemberReferenceKind.Method || m.Parent.Kind != HandleKind.TypeReference) continue;
        var reference = (TypeReferenceHandle)m.Parent;
        if (!Framework(RefAssembly(mod, reference)) || !ResolveRef(mod, reference, out var found)) continue;
        checkedMethods++; string shape = Shape(mod, m.Signature, mod.GetString(m.Name));
        if (!HasMethod(found, shape, new HashSet<string>(StringComparer.Ordinal))) missing.Add("METHOD " + RefName(mod, reference) + "::" + shape);
    }
    foreach (string item in missing) Console.WriteLine("MISSING " + item);
    Console.WriteLine($"TRIMMED_RUNTIME types={checkedTypes}, direct-method-shapes={checkedMethods}, missing={missing.Count}"); return missing.Count == 0 ? 0 : 1;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
finally { for (int i = opened.Count - 1; i >= 0; i--) opened[i].Dispose(); }
