using System;
using System.Collections.Generic;

namespace MycopunkBuildArchitect
{
    [Serializable]
    public sealed class ProfileDatabase
    {
        public List<BuildProfile> Profiles = new List<BuildProfile>();
    }

    [Serializable]
    public sealed class BuildProfile
    {
        public string GearKey = "Unknown gear";
        public string Name = "My build";
        public List<ProfileItem> Items = new List<ProfileItem>();
    }

    [Serializable]
    public sealed class ProfileItem
    {
        public string SourceId = "";
        public string DisplayName = "";
        public int Count = 1;
    }
}
