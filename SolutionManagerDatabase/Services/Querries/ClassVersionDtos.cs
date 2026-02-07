using System;
using System.Collections.Generic;

namespace SolutionManagerDatabase.Services.Queries;

public sealed record ClassMemberDto(
    string MemberKind,          // Field / Property
    string MemberName,
    string TypeDisplay,
    string TypeRaw,
    bool IsNullable,
    bool IsCollection,
    string? ElementTypeRaw,
    string? AttributesRaw,
    bool IsRequired,
    bool IsKey,
    int? MaxLength,
    int? MinLength,
    string? SqlTypeName,
    string? DataType,
    string? DisplayName,
    string? DisplayFormatString,
    bool DisplayFormatApplyInEditMode,
    string? ForeignKey,
    string? InverseProperty,
    bool HasGetter,
    bool HasSetter,
    bool IsInitOnly,
    bool IsStatic,
    bool IsAbstract,
    bool IsVirtual,
    bool IsOverride,
    int SpanStart
);

public sealed record ClassVersionDto(
    string RepositoryName,
    string SolutionName,
    string ProjectName,

    long ArtifactId,
    string LogicalClassKey,
    string ClassName,
    string? Namespace,

    string? Module,
    string? Visibility,
    string? Feature,

    string RelativeFilePath,
    string FileName,

    string? BaseClassName,
    string? BaseTypeName,
    string? InterfacesRaw,
    bool IsAbstract,
    bool IsStatic,
    bool IsPartial,

    string? FileSha256,
    DateTime? FileLastWriteUtc,
    long? FileSizeBytes,

    IReadOnlyList<ClassMemberDto> Members
);
