using Microsoft.AspNetCore.Mvc;
using SolutionManagerDatabase.Services.Queries;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SolutionManager.Controllers;

public sealed class CompareController : Controller
{
    private readonly IClassQueryService _classes;

    public CompareController(IClassQueryService classes)
    {
        _classes = classes;
    }

    // /Compare/Class?key=Schema|Contact|Terrafirma.Contacts.Schema.DbContact
    // /Compare/Class?name=DbContact&module=Contact&visibility=Schema&feature=
    [HttpGet]
    public async Task<IActionResult> Class(string? name, string? module, string? visibility, string? feature, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        module = (module ?? "").Trim();
        visibility = (visibility ?? "").Trim();
        feature = (feature ?? "").Trim();

        ViewBag.Name = name;
        ViewBag.Module = module;
        ViewBag.Visibility = visibility;
        ViewBag.Feature = feature;

        ViewBag.ClassNames = await _classes.GetDistinctClassNamesAsync(ct) ?? Array.Empty<string>();
        ViewBag.Modules = await _classes.GetDistinctModulesAsync(ct) ?? Array.Empty<string>();
        ViewBag.Visibilities = await _classes.GetDistinctVisibilitiesAsync(ct) ?? Array.Empty<string>();
        ViewBag.Features = await _classes.GetDistinctFeaturesAsync(ct) ?? Array.Empty<string>();

        if (name.Length == 0)
            return View(model: null);

        var matrix = await _classes.GetClassCompareMatrixAsync(name, module, visibility, feature, ct);


        return View(matrix);
    }

}
