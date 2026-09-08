using System;

namespace Orbiters.ReFit.Editor.Tests
{
    public static class ReFitCommissionImageTests
    {
        public static void RunOrThrow()
        {
            const string api = "https://api.orbiters.cc/refit";
            Check(ReFitCommissionClient.NormalizeMediaUrl("/files/serve/banner", api),
                "https://api.orbiters.cc/files/serve/banner?format=png");
            Check(ReFitCommissionClient.NormalizeMediaUrl("/files/serve/banner?v=2&format=webp#preview", api),
                "https://api.orbiters.cc/files/serve/banner?v=2&format=png#preview");
            Check(ReFitCommissionClient.NormalizeMediaUrl("http://localhost:4100/files/serve/banner?format=png&v=2", "http://localhost:4100/refit"),
                "http://localhost:4100/files/serve/banner?format=png&v=2");
            const string external = "https://cdn.example.invalid/files/serve/banner?format=webp";
            Check(ReFitCommissionClient.NormalizeMediaUrl(external, api), external);
            Check(ReFitCommissionClient.NormalizeMediaUrl("/other/banner?v=2", api), "https://api.orbiters.cc/other/banner?v=2");
            Check(ReFitCommissionClient.NormalizeMediaUrl(null, api), null);
            // Exercises the installed MCB bridge, or the standalone path when MCB is absent.
            string integrated = ReFitCommissionClient.NormalizeMediaUrl("/files/serve/banner?v=2");
            if (!integrated.Contains("v=2&format=png")) throw new Exception("Commission image bridge did not request PNG: " + integrated);
        }

        private static void Check(string actual, string expected)
        {
            if (actual != expected) throw new Exception("Commission image URL: expected " + expected + ", got " + actual);
        }
    }
}
