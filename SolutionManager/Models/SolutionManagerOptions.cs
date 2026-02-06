namespace SolutionManager.Models
{
    public sealed class SolutionManagerOptions
    {
        /// <summary>
        /// Where all repos live locally (e.g. C:\Development\).
        /// </summary>
        public string DevRootPath { get; set; } = default;

        /// <summary>
        /// Default remote repo URL root (used if you only store repo name).
        /// Example: https://github.com/johnmackerras/
        /// </summary>
        public string DefaultRemoteRepoRootUrl { get; set; } =  default;
    }
}
