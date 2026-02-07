using System.Collections.Generic;

namespace SolutionManagerDatabase.Services.Queries;

public sealed record MatrixColumnDto(
    int Index,
    string RepositoryName,
    string SolutionName,
    string ProjectName,
    long ArtifactId,
    string RelativeFilePath,
    string? FileSha256
);

public sealed record MatrixCellDto(
    int ColumnIndex,
    string? TypeDisplay,     // null means '---'
    string? TypeRaw,
    bool? IsRequired,
    int? MaxLength,
    string? SqlTypeName
);

public sealed record MatrixRowDto(
    string MemberKind,       // Field / Property
    string MemberName,
    IReadOnlyList<MatrixCellDto> Cells
);

public sealed record ClassCompareMatrixDto(
    string LogicalClassKey,
    string? Module,
    string? Visibility,
    string? Feature,
    string? Namespace,
    string ClassName,
    IReadOnlyList<MatrixColumnDto> Columns,
    IReadOnlyList<MatrixRowDto> Rows
);
