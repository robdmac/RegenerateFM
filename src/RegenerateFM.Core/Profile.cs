using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace RegenerateFM
{
    // The profile read from FM26's screen. Stat values are an int (1-20) or a
    // scouted range string such as "12-15".
    public sealed class ProfileSnapshot
    {
        public string Type = "player";
        public string Name;
        public int Age;
        public string Nationality;
        public string JobTitle;
        public string Position;
        public string Club;
        public string Height;
        public string BiographyHeadline;
        public string Gender;  // "female", "male", or null when the game didn't say
        // FM's ethnicity for the person (one of FaceGenerator.Ethnicities), from a
        // match the game drew in 3D, a newgen's appearance or the user's choice;
        // null when not known, and the prompt asks for a best guess. SkinTone (one
        // of SkinTones) and HairColour (one of HairColours) come only from FM.
        public string Ethnicity;
        public string SkinTone;
        public string HairColour;
        // A real person rather than one of FM's newgens: the prompt asks for
        // their own likeness, with appearance traits for whatever the image
        // model can't know of them.
        public bool Likeness;
        // The game's groups of people the person favours, with their IDs:
        // ("Brothers", "29232937"); only family groups count (FamilyRelatives).
        public List<(string Relation, string PersonId)> Relatives = new List<(string Relation, string PersonId)>();
        public Dictionary<string, object> Physical = new Dictionary<string, object>();
        public Dictionary<string, object> Technical = new Dictionary<string, object>();
        public Dictionary<string, object> Mental = new Dictionary<string, object>();
        public Dictionary<string, object> Goalkeeping = new Dictionary<string, object>();
    }
}
