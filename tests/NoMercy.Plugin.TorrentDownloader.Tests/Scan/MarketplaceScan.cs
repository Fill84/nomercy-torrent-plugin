using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Scan;

/// <summary>
/// What the marketplace's code scan refuses in a plugin folder, in the words it
/// refuses it with.
/// </summary>
/// <remarks>
/// <para>
/// The scan is the media server's (<c>PluginCodeScanner</c> on its <c>dev</c>
/// branch) and is internal to it, so it cannot be called from here. This is the
/// same rule list, written here from what that file and media-server #476 say,
/// so that a change to the plugin shows what it removed from the refusal and
/// that it added nothing. The rules are listed in <c>SPRINTS.md</c> § S16.
/// </para>
/// <para>
/// The server stopped running it at load and install on 8 October 2026
/// (media-server #504); the marketplace runs it before it lists a plugin. A
/// plugin it refuses loads from a zip and is never in the marketplace.
/// </para>
/// </remarks>
internal static class MarketplaceScan
{
    /// <summary>
    /// The assemblies the host shares with every plugin. A file under one of these
    /// names is never loaded from the plugin folder, so it is not read.
    /// </summary>
    private static readonly HashSet<string> SharedAssemblies =
    [
        "NoMercy.PluginSdk.Abstractions",
        "NoMercy.PluginSdk.Mvc",
        "NoMercy.Events",
        "NoMercy.Design",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.DependencyInjection",
        "Newtonsoft.Json",
        "Ulid",
    ];

    private const string PluginAssemblyPrefix = "NoMercy.Plugin.";

    private static readonly string[] BannedNamespaces =
    [
        "System.Reflection.",
        "System.Runtime.InteropServices.",
        "System.Runtime.Loader",
        "Microsoft.CSharp.RuntimeBinder",
    ];

    private static readonly HashSet<string> BannedTypes =
    [
        "System.AppDomain",
        "System.Activator",
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
    ];

    private static readonly HashSet<string> BannedMembers =
    [
        "System.Type::GetType",
        "System.Environment::Exit",
        "System.Environment::FailFast",
        "System.Delegate::DynamicInvoke",
        "System.Linq.Expressions.LambdaExpression::Compile",
        "System.Linq.Expressions.Expression`1::Compile",
    ];

    /// <summary>
    /// Types in a banned namespace that the compiler itself writes into safe code
    /// (a collection expression into a span or a list), so they are judged by
    /// member rather than refused outright.
    /// </summary>
    private static readonly HashSet<string> CompilerHelperTypes =
    [
        "System.Runtime.CompilerServices.Unsafe",
        "System.Runtime.InteropServices.MemoryMarshal",
        "System.Runtime.InteropServices.CollectionsMarshal",
        "System.Runtime.InteropServices.ImmutableCollectionsMarshal",
    ];

    /// <summary>The members of those types the compiler emits, by their byref overloads only.</summary>
    private static readonly HashSet<string> CompilerEmittedMembers =
    [
        "System.Runtime.CompilerServices.Unsafe::As",
        "System.Runtime.CompilerServices.Unsafe::AsRef",
        "System.Runtime.CompilerServices.Unsafe::Add",
        "System.Runtime.InteropServices.MemoryMarshal::CreateSpan",
        "System.Runtime.InteropServices.MemoryMarshal::CreateReadOnlySpan",
        "System.Runtime.InteropServices.CollectionsMarshal::SetCount",
        "System.Runtime.InteropServices.CollectionsMarshal::AsSpan",
        "System.Runtime.InteropServices.ImmutableCollectionsMarshal::AsImmutableArray",
    ];

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>
    /// Every finding in <paramref name="folder"/>, as <c>path: finding</c> with the
    /// path relative to the folder and in forward slashes, sorted.
    /// </summary>
    /// <remarks>
    /// Every file that is an image counts, at any depth and under any name — the
    /// load context serves whatever is in the folder — so the bytes decide, not
    /// the extension alone.
    /// </remarks>
    public static IReadOnlyList<string> Folder(string folder, string entryFileName)
    {
        List<string> findings = [];

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            using FileStream stream = File.OpenRead(file);

            if (!IsImage(file, stream))
            {
                continue;
            }

            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            bool entry = string.Equals(Path.GetFileName(file), entryFileName, StringComparison.OrdinalIgnoreCase)
                         && relative == Path.GetFileName(file);

            findings.AddRange(Image(stream, Path.GetFileName(file), entry).Select(finding => $"{relative}: {finding}"));
        }

        findings.Sort(StringComparer.Ordinal);

        return findings;
    }

    /// <summary>The findings for one image; empty means it passes.</summary>
    public static IReadOnlyList<string> Image(Stream image, string fileName, bool entry)
    {
        List<string> findings = [];

        try
        {
            using PEReader pe = new(image, PEStreamOptions.LeaveOpen);
            CorHeader? cor = pe.HasMetadata ? pe.PEHeaders.CorHeader : null;

            if (cor is null || !cor.Flags.HasFlag(CorFlags.ILOnly))
            {
                return ["not pure IL"];
            }

            MetadataReader metadata = pe.GetMetadataReader();
            string assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);

            if (!entry
                && SharedAssemblies.Contains(assemblyName)
                && string.Equals(Path.GetFileNameWithoutExtension(fileName), assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            TypeReferences(metadata, findings);
            MemberReferences(metadata, findings);
            Definitions(pe, metadata, findings);
        }
        catch (BadImageFormatException)
        {
            return ["not pure IL"];
        }
        catch (Exception failure)
        {
            // The marketplace refuses what it cannot read, in these words.
            findings.Add($"unreadable: {failure.GetType().Name}");
        }

        return findings;
    }

    /// <summary>
    /// The banned members and types the code of one type reaches, as
    /// <c>Namespace.Type::Member</c> (or the type alone), sorted.
    /// </summary>
    /// <remarks>
    /// The compiler puts a lambda's body and an async method's state machine in
    /// nested types it names itself, so every type nested inside counts as the
    /// type's own.
    /// </remarks>
    public static IReadOnlyList<string> ReachedFrom(Stream image, string typeFullName)
    {
        using PEReader pe = new(image, PEStreamOptions.LeaveOpen);
        MetadataReader metadata = pe.GetMetadataReader();
        SortedSet<string> reached = new(StringComparer.Ordinal);

        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            if (OutermostName(metadata, handle) != typeFullName)
            {
                continue;
            }

            foreach (MethodDefinitionHandle method in metadata.GetTypeDefinition(handle).GetMethods())
            {
                int address = metadata.GetMethodDefinition(method).RelativeVirtualAddress;

                if (address == 0)
                {
                    continue;
                }

                foreach (EntityHandle token in Tokens(pe.GetMethodBody(address).GetILReader()))
                {
                    if (BannedReach(metadata, token) is string banned)
                    {
                        reached.Add(banned);
                    }
                }
            }
        }

        return [.. reached];
    }

    private static string OutermostName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);

        while (!type.GetDeclaringType().IsNil)
        {
            type = metadata.GetTypeDefinition(type.GetDeclaringType());
        }

        string ns = metadata.GetString(type.Namespace);

        return ns.Length == 0 ? metadata.GetString(type.Name) : $"{ns}.{metadata.GetString(type.Name)}";
    }

    /// <summary>Every metadata token an instruction names: a method, a field, a type.</summary>
    private static IEnumerable<EntityHandle> Tokens(BlobReader il)
    {
        List<EntityHandle> tokens = [];

        while (il.RemainingBytes > 0)
        {
            short value = il.ReadByte();

            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il.ReadByte());
            }

            OpCode code = OpCodesByValue[value];

            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField
                or OperandType.InlineType or OperandType.InlineTok)
            {
                tokens.Add(MetadataTokens.EntityHandle(il.ReadInt32()));

                continue;
            }

            int operand = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 * il.ReadInt32(),
                _ => 4,
            };

            il.Offset += operand;
        }

        return tokens;
    }

    /// <summary>
    /// The banned member or type a token names, in the same terms the folder scan
    /// judges by, or null.
    /// </summary>
    private static string? BannedReach(MetadataReader metadata, EntityHandle token)
    {
        if (token.Kind == HandleKind.MethodSpecification)
        {
            token = metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method;
        }

        if (token.Kind == HandleKind.TypeReference)
        {
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)token);
            string ns = metadata.GetString(type.Namespace);
            string name = metadata.GetString(type.Name);

            return IsBanned(ns, name) ? $"{ns}.{name}" : null;
        }

        if (token.Kind != HandleKind.MemberReference)
        {
            return null;
        }

        MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)token);
        string? parent = ParentName(metadata, member.Parent);

        if (parent is null)
        {
            return null;
        }

        string memberName = metadata.GetString(member.Name);
        string key = $"{parent}::{memberName}";
        int split = parent.LastIndexOf('.');
        bool bannedParent = IsBanned(parent[..split], parent[(split + 1)..])
                            && !(parent == "System.Reflection.MemberInfo" && memberName == "get_Name");

        return bannedParent
               || BannedMembers.Contains(key)
               || (CompilerHelperTypes.Contains(parent) && !IsCompilerEmitted(member, key))
            ? key
            : null;
    }

    private static bool IsImage(string fileName, Stream stream)
    {
        Span<byte> head = stackalloc byte[2];
        int read = stream.ReadAtLeast(head, 2, throwOnEndOfStream: false);
        stream.Position = 0;

        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               || (read == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z');
    }

    private static void TypeReferences(MetadataReader metadata, List<string> findings)
    {
        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            TypeReference type = metadata.GetTypeReference(handle);

            if (type.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                string assembly = metadata.GetString(
                    metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);

                if (assembly.StartsWith("NoMercy.", StringComparison.Ordinal)
                    && !assembly.StartsWith(PluginAssemblyPrefix, StringComparison.Ordinal)
                    && !SharedAssemblies.Contains(assembly))
                {
                    Add(findings, $"references server assembly {assembly}");
                }
            }

            string ns = metadata.GetString(type.Namespace);
            string name = metadata.GetString(type.Name);

            if (IsBanned(ns, name) && !ReadsOnlyTheName(metadata, handle, ns, name))
            {
                Add(findings, $"references banned type {ns}.{name}");
            }
        }
    }

    private static bool IsBanned(string ns, string name)
    {
        return ns switch
        {
            // Attributes are written by the compiler into every assembly; the
            // types that act are the ban.
            "System.Reflection" => !name.EndsWith("Attribute", StringComparison.Ordinal) && name != "AssemblyName",
            "System.Runtime.InteropServices" => !name.EndsWith("Attribute", StringComparison.Ordinal)
                                                && !CompilerHelperTypes.Contains($"{ns}.{name}"),
            _ => BannedTypes.Contains($"{ns}.{name}")
                 || BannedNamespaces.Any(banned => ns.StartsWith(banned, StringComparison.Ordinal)),
        };
    }

    /// <summary>
    /// <c>ex.GetType().Name</c> binds to <c>MemberInfo.get_Name</c>: it reads a
    /// name and acts on nothing, so a <c>MemberInfo</c> used for that alone passes.
    /// </summary>
    private static bool ReadsOnlyTheName(MetadataReader metadata, TypeReferenceHandle handle, string ns, string name)
    {
        if (ns != "System.Reflection" || name != "MemberInfo")
        {
            return false;
        }

        List<string> members = [.. metadata.MemberReferences
            .Select(metadata.GetMemberReference)
            .Where(member => member.Parent == (EntityHandle)handle)
            .Select(member => metadata.GetString(member.Name))];

        return members.Count > 0 && members.All(member => member == "get_Name");
    }

    private static void MemberReferences(MetadataReader metadata, List<string> findings)
    {
        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            string? parent = ParentName(metadata, member.Parent);

            if (parent is null)
            {
                continue;
            }

            string key = $"{parent}::{metadata.GetString(member.Name)}";

            if (BannedMembers.Contains(key)
                || (CompilerHelperTypes.Contains(parent) && !IsCompilerEmitted(member, key)))
            {
                Add(findings, $"calls banned member {key}");
            }
        }
    }

    /// <summary>
    /// The compiler emits <c>Unsafe.As&lt;TFrom, TTo&gt;(ref TFrom)</c>; the
    /// one-type-argument <c>Unsafe.As&lt;T&gt;(object)</c> is an unchecked cast a
    /// person wrote. Pointer overloads are always a person's.
    /// </summary>
    private static bool IsCompilerEmitted(MemberReference member, string key)
    {
        if (!CompilerEmittedMembers.Contains(key) || member.GetKind() != MemberReferenceKind.Method)
        {
            return false;
        }

        MethodSignature<bool> signature = member.DecodeMethodSignature(PointerFinder.Instance, null);

        return !signature.ParameterTypes.Contains(true)
               && (key != "System.Runtime.CompilerServices.Unsafe::As" || signature.GenericParameterCount == 2);
    }

    private static string? ParentName(MetadataReader metadata, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeSpecification)
        {
            TypeSpecification specification = metadata.GetTypeSpecification((TypeSpecificationHandle)parent);
            BlobReader blob = metadata.GetBlobReader(specification.Signature);

            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            {
                return null;
            }

            blob.ReadSignatureTypeCode();
            parent = blob.ReadTypeHandle();
        }

        if (parent.Kind != HandleKind.TypeReference)
        {
            return null;
        }

        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)parent);

        return $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";
    }

    private static void Definitions(PEReader pe, MetadataReader metadata, List<string> findings)
    {
        foreach (FieldDefinitionHandle handle in metadata.FieldDefinitions)
        {
            FieldDefinition field = metadata.GetFieldDefinition(handle);

            if (field.DecodeSignature(PointerFinder.Instance, null))
            {
                Add(findings, $"Pointer field {Name(metadata, field.GetDeclaringType(), field.Name)}");
            }
        }

        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            string name = Name(metadata, method.GetDeclaringType(), method.Name);

            // A delegate's body is supplied by the runtime (code type Runtime), so
            // only Native, OPTIL and Unmanaged mean code the scan cannot read.
            MethodImplAttributes codeType = method.ImplAttributes & MethodImplAttributes.CodeTypeMask;

            if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl)
                || method.ImplAttributes.HasFlag(MethodImplAttributes.Unmanaged)
                || codeType is MethodImplAttributes.Native or MethodImplAttributes.OPTIL)
            {
                Add(findings, $"P/Invoke or native method {name}");
            }

            MethodSignature<bool> signature = method.DecodeSignature(PointerFinder.Instance, null);

            if (signature.ReturnType || signature.ParameterTypes.Contains(true))
            {
                Add(findings, $"Pointer in signature of {name}");
            }

            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);

            if (!body.LocalSignature.IsNil
                && metadata.GetStandaloneSignature(body.LocalSignature)
                    .DecodeLocalSignature(PointerFinder.Instance, null)
                    .Contains(true))
            {
                Add(findings, $"Pointer local in {name}");
            }

            if (UsesCalli(body.GetILReader()))
            {
                Add(findings, $"calli in {name}");
            }
        }
    }

    private static bool UsesCalli(BlobReader il)
    {
        while (il.RemainingBytes > 0)
        {
            short value = il.ReadByte();

            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il.ReadByte());
            }

            OpCode code = OpCodesByValue[value];

            if (code == OpCodes.Calli)
            {
                return true;
            }

            // Worked out before it is added, never in the same statement: a switch
            // reads its target count, which moves the reader, and `Offset +=` would
            // have read the offset before that move and lost four bytes.
            int operand = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 * il.ReadInt32(),
                _ => 4,
            };

            il.Offset += operand;
        }

        return false;
    }

    private static string Name(MetadataReader metadata, TypeDefinitionHandle type, StringHandle member)
    {
        return $"{metadata.GetString(metadata.GetTypeDefinition(type).Name)}.{metadata.GetString(member)}";
    }

    private static void Add(List<string> findings, string finding)
    {
        if (!findings.Contains(finding))
        {
            findings.Add(finding);
        }
    }

    /// <summary>True when a signature holds a pointer or a function pointer anywhere.</summary>
    private sealed class PointerFinder : ISignatureTypeProvider<bool, object?>
    {
        public static readonly PointerFinder Instance = new();

        public bool GetPointerType(bool elementType) => true;

        public bool GetFunctionPointerType(MethodSignature<bool> signature) => true;

        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;

        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => false;

        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => false;

        public bool GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => false;

        public bool GetSZArrayType(bool elementType) => elementType;

        public bool GetArrayType(bool elementType, ArrayShape shape) => elementType;

        public bool GetByReferenceType(bool elementType) => elementType;

        public bool GetPinnedType(bool elementType) => elementType;

        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) =>
            genericType || typeArguments.Contains(true);

        public bool GetGenericMethodParameter(object? genericContext, int index) => false;

        public bool GetGenericTypeParameter(object? genericContext, int index) => false;

        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => unmodifiedType;
    }
}
