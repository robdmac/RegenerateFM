using System;
using System.IO;
using Xunit;

namespace RegenerateFM.Tests
{
    // AgedFaces: which faces are two or more years old, FM's date in any
    // language's order, and the once-a-season record.
    public class AgedFacesTests
    {
        [Fact]
        public void AFaceTwoOrMoreYearsYoungerIsAged()
        {
            Assert.True(AgedFaces.IsAged(17, 19));
            Assert.True(AgedFaces.IsAged(17, 25));
            Assert.False(AgedFaces.IsAged(17, 18));
            Assert.False(AgedFaces.IsAged(17, 17));
            // Made before RegenerateFM noted ages, or an age the list didn't show.
            Assert.False(AgedFaces.IsAged(null, 30));
            Assert.False(AgedFaces.IsAged(17, 0));
        }

        // FM27's squad lists show no ages, so a face made in a known year goes by the game's year.
        [Fact]
        public void AFaceMadeTwoOrMoreSeasonsAgoIsAged()
        {
            Assert.True(AgedFaces.IsAged(17, 0, 2025, 2027));
            Assert.False(AgedFaces.IsAged(17, 0, 2026, 2027));
            // The year decides when both are known.
            Assert.False(AgedFaces.IsAged(17, 19, 2026, 2027));
            // The date not found: the age, as before.
            Assert.True(AgedFaces.IsAged(17, 19, 2026, null));
        }

        // The year a face was made in is kept with it, and brought back with it.
        [Fact]
        public void AFaceKeepsTheYearItWasMadeIn()
        {
            string folder = Path.Combine(Path.GetTempPath(), "regeneratefm-aged-" + Guid.NewGuid().ToString("N"));
            try
            {
                Portraits.SavePortrait(folder, "2000463197", Pngs.Make(64), owner: "Liam Rooney", age: 18, gameYear: 2027);
                Assert.Equal(2027, FaceHistory.GameYear(folder, "2000463197"));
                Assert.Equal(18, FaceHistory.Age(folder, "2000463197"));
                Portraits.SavePortrait(folder, "2000463197", Pngs.Make(64), owner: "Liam Rooney", age: 20, gameYear: 2029);
                Assert.Equal(2029, FaceHistory.GameYear(folder, "2000463197"));
                FaceHistory.Previous(folder, "2000463197", "Liam Rooney");
                Assert.Equal(2027, FaceHistory.GameYear(folder, "2000463197"));
                // A face from before the year was noted.
                Portraits.SavePortrait(folder, "61097660", Pngs.Make(64), owner: "Mark Weir", age: 35);
                Assert.Null(FaceHistory.GameYear(folder, "61097660"));
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        [Theory]
        [InlineData("28/3/2027", 2027)]
        [InlineData("SUN 28/3/2027", 2027)]
        [InlineData("3/28/2027", 2027)]
        [InlineData("2027/3/28", 2027)]
        [InlineData("So. 28.3.2027", 2027)]
        [InlineData("28-03-2031", 2031)]
        [InlineData("日 2027/03/28", 2027)]
        public void TheYearIsReadFromFmsDate(string shown, int year) => Assert.Equal(year, AgedFaces.GameYear(shown));

        [Theory]
        [InlineData("Forfar in 7 days")]
        [InlineData("18:00")]
        [InlineData("2027")]
        [InlineData("Season 2026/27")]
        [InlineData("12/3/1850")]
        [InlineData(null)]
        public void OtherTextIsNotADate(string shown) => Assert.Null(AgedFaces.GameYear(shown));

        [Fact]
        public void EachYearIsOfferedOnce()
        {
            string folder = Path.Combine(Path.GetTempPath(), "regeneratefm-aged-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, AgedFaces.FileName);
            try
            {
                Assert.False(AgedFaces.Offered(path, "FM27", 2027));
                AgedFaces.Record(path, "FM27", 2027);
                Assert.True(AgedFaces.Offered(path, "FM27", 2027));
                // An earlier year (another save) is covered too; a later one isn't.
                Assert.True(AgedFaces.Offered(path, "FM27", 2026));
                Assert.False(AgedFaces.Offered(path, "FM27", 2028));
                Assert.False(AgedFaces.Offered(path, "FM26", 2027));
                File.WriteAllText(path, "not json");
                Assert.False(AgedFaces.Offered(path, "FM27", 2027));
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void TheOfferCanBeTurnedOff()
        {
            string folder = Path.Combine(Path.GetTempPath(), "regeneratefm-aged-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, Settings.FileName);
            try
            {
                // A new settings.ini offers it, and says how to turn it off.
                Assert.True(Settings.Load(path).OfferAgedFaces);
                Assert.Contains("OfferAgedFaces = true", File.ReadAllText(path));
                File.WriteAllText(path, "[Faces]\nOfferAgedFaces = false\n");
                Assert.False(Settings.Load(path).OfferAgedFaces);
                File.WriteAllText(path, "[OpenAI]\nQuality = low\n");
                Assert.True(Settings.Load(path).OfferAgedFaces);
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }
    }
}
