using System.Reflection;
using System.Runtime.Loader;

namespace SbStudio.Effects.Host;

internal sealed class ModuleSession : IDisposable
{
    private readonly ModuleLoadContext context;
    private readonly Assembly assembly;
    internal PackageManifest Manifest { get; }
    internal ModuleSession(string root)
    {
        Manifest = PackageFiles.ReadManifest(root);
        foreach (string dependency in Manifest.Dependencies)
        {
            string path = PackageFiles.SafePath(root, dependency);
            var identity = AssemblyName.GetAssemblyName(path);
            if (!string.Equals(identity.Name, Path.GetFileNameWithoutExtension(dependency), StringComparison.OrdinalIgnoreCase) ||
                identity.Name == typeof(IEffectModule).Assembly.GetName().Name)
                throw new FileLoadException("Dependency file name does not match its declared assembly identity.");
        }
        context = new(root, Manifest); assembly = context.ReadAssembly(PackageFiles.SafePath(root, Manifest.EntryAssembly));
    }
    internal (IEffectModule Module, EffectDefinition Definition) Create(EffectEntry entry)
    {
        var type = assembly.GetType(entry.EntryType, true)!;
        if (!typeof(IEffectModule).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null) throw new InvalidOperationException("Invalid effect entry type: " + entry.EntryType);
        var module = (IEffectModule)Activator.CreateInstance(type)!; var builder = new EffectDefinitionBuilder(entry.EffectId);
        module.Define(builder); return (module, builder.Build());
    }
    public void Dispose() => context.Unload();
    private sealed class ModuleLoadContext(string root, PackageManifest manifest) : AssemblyLoadContext(isCollectible: true)
    {
        internal Assembly ReadAssembly(string path)
        {
            // Do not retain a Windows file mapping into the request's temporary directory.
            using var dll = File.OpenRead(path);
            string symbols = Path.ChangeExtension(path, ".pdb");
            using var pdb = File.Exists(symbols) ? File.OpenRead(symbols) : null;
            return LoadFromStream(dll, pdb);
        }
        protected override Assembly? Load(AssemblyName name)
        {
            var sdk = typeof(IEffectModule).Assembly;
            if (name.Name == sdk.GetName().Name)
            {
                if (name.Version?.Major != sdk.GetName().Version?.Major) throw new FileLoadException("Effect SDK assembly version mismatch.");
                return sdk;
            }
            string? path = manifest.Dependencies.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(name.Name, StringComparison.OrdinalIgnoreCase));
            if (path != null) return ReadAssembly(PackageFiles.SafePath(root, path));
            if (name.Name?.StartsWith("System.", StringComparison.Ordinal) == true || name.Name is "System" or "netstandard" or "mscorlib" or "Microsoft.CSharp") return null;
            throw new FileNotFoundException("Undeclared effect dependency: " + name.Name);
        }
        protected override nint LoadUnmanagedDll(string name) => throw new NotSupportedException("Native effect dependencies are not supported.");
    }
}
