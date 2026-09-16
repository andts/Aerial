using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aerial
{
    /**
     * Class for holding global variables such as URLs to the github repo for easy changing.  Origionally had them
     * in a app.config file, but that requires the config file to be in the same directory as the executable, 
     * so the install instructions would have to be changed
     */
    static class AerialGlobalVars
    {
        // URL for the JSON document that describes the latest github release
        public static string githubLatestReleaseDetails = "https://api.github.com/repos/cdima/aerial/releases/latest";
        //Link to the github releases page
        public static string githubAllReleases = "https://github.com/cDima/Aerial/releases";
        // Both URLs below are dead/near-empty as of 2026 and are no longer used as defaults - the
        // app ships a bundled catalog instead (see AerialContext.GetAllEntries and Videos.json).
        // They are kept only so RegSettings.MigrateIfNeeded can recognize and clear them from an
        // existing install's JsonURL setting.
        //
        //link to the apple videos - Apple retired this feed; returns HTTP 403.
        public static string appleVideosURI = "http://a1.phobos.apple.com/us/r1000/000/Features/atv/AutumnResources/videos/entries.json";
        //link to the 4k apple videos (hosted by Jonathon Powell) - still responds, but only serves
        //9 assets across 4 distinct locations.
        public static string applefourKVideoURI = "https://t27q97zg19.execute-api.us-east-1.amazonaws.com/prod/aerialAltJSON/4kEntites.json";
        //original link to 4k apple videos, we can't parse this currently so I hosted a modified version
        //public static string applefourKVideoURI = "https://sylvan.apple.com/Aerials/2x/entries.json";
    }
}
