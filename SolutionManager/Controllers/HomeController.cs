using Microsoft.AspNetCore.Mvc;
using SolutionManager.Models.System;
using SolutionManagerDatabase.Services;
using System.Diagnostics;
using System.Threading;
using SolutionManagerDatabase.Services;
using System.Threading.Tasks;

namespace SolutionManager.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ScanSolutions([FromServices] ISolutionScanService scan, CancellationToken ct)
        {
            var touched = await scan.ScanAllRepositoriesAsync(ct);
            TempData["StatusMessage"] = $"Scan complete. Solutions touched: {touched}.";
            return RedirectToAction(nameof(Index));
        }


        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
