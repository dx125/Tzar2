using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>
/// ADR 0213 (code review 2026-10, item 16): the AI reads the world through read-only views (<c>World.PlayersView</c>,
/// <c>KingdomsView</c>, the stores' <c>View</c>); the writable spans (<c>Players</c>, <c>Kingdoms</c>,
/// <c>ComponentStore.Components</c>) are the systems'. Its IL must not reference them, so a write cannot slip in through a
/// <c>ref</c> to them; it changes the world only through commands (REQ-AI-03, ADR 0051).
/// </summary>
public class AiReadOnlyTests
{
    private static readonly string[] Writable =
    [
        "Tzar2.Sim.World::get_Players", "Tzar2.Sim.World::get_Kingdoms", "Tzar2.Sim.Ecs.ComponentStore`1::get_Components",
    ];

    [Fact]
    public void The_ai_reads_the_world_only_through_read_only_views()
    {
        using var stream = File.OpenRead(typeof(AI.AIAssembly).Assembly.Location);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        int views = 0;
        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            string? owner = member.Parent.Kind switch
            {
                HandleKind.TypeReference => Name(metadata, (TypeReferenceHandle)member.Parent),
                HandleKind.TypeSpecification => metadata.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(new GenericNames(metadata), null),
                _ => null,
            };
            string name = $"{owner}::{metadata.GetString(member.Name)}";
            views += name is "Tzar2.Sim.World::get_PlayersView" || name.EndsWith("::get_View", StringComparison.Ordinal) ? 1 : 0;
            if (Array.IndexOf(Writable, name) >= 0)
            {
                found.Add(name);
            }
        }

        Assert.True(found.Count == 0, "Tzar2.AI takes writable spans of the world (ADR 0213): " + string.Join(", ", found));
        Assert.True(views > 0, "the check found no read-only view at all");
    }

    private static string Name(MetadataReader metadata, TypeReferenceHandle handle)
    {
        TypeReference type = metadata.GetTypeReference(handle);
        string ns = metadata.GetString(type.Namespace);
        return (ns.Length > 0 ? ns + "." : string.Empty) + metadata.GetString(type.Name);
    }

    // The generic type of an instantiation (ComponentStore`1 of ComponentStore<Worker>); other shapes by their kind.
    private sealed class GenericNames(MetadataReader metadata) : ISignatureTypeProvider<string, object?>
    {
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Name(metadata, handle);

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "spec";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => elementType + "[]";
    }
}
