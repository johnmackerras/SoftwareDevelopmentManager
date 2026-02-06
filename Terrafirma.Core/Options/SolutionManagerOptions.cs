namespace Terrafirma.Core.Options
{
    public sealed class SolutionManagerOptions
    {
        public string GitRootPath { get; set; } = default!;
        public string DefaultRemoteRepoRootUrl { get; set; } =  default!;
    }
}
