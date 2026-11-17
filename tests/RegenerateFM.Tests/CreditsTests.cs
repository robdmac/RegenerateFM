using System;
using System.Collections.Generic;
using Xunit;

namespace RegenerateFM.Tests
{
    // What the game tells a player about RegenerateFM credits: the balance,
    // what's left after a face, and the free faces a new account starts with,
    // against a stand-in for the credits service.
    public class CreditsTests
    {
        private static Credits.Account Balance(StandIn service, string token = null) =>
            Credits.Balance(service.Url, token ?? service.Token).GetAwaiter().GetResult();

        [Fact]
        public void TheBalanceSaysWhetherAPackWasBoughtAndHowManyFreeFacesAreLeft()
        {
            using var service = new StandIn
            {
                BalanceAnswer = () => new { email = "player@example.com", credits = 2, bought = false, free = 2, buy_url = "x" },
            };
            var account = Balance(service);
            Assert.Equal(("player@example.com", 2, false, 2, true), (account.Email, account.Credits, account.Bought, account.Free, account.AllFree));
            var (method, path, authorization, _, _, agent) = service.Received[0];
            Assert.Equal(("GET", "/v1/balance", "Bearer " + service.Token, "RegenerateFM"), (method, path, authorization, agent));

            // A buyer's credits aren't free faces, even with some welcome credits unspent.
            service.BalanceAnswer = () => new { email = "player@example.com", credits = 77, bought = true, free = 2, buy_url = "x" };
            Assert.False(Balance(service).AllFree);
            // Credits granted by hand to someone who has never bought aren't free faces either.
            service.BalanceAnswer = () => new { email = "player@example.com", credits = 5, bought = false, free = 3, buy_url = "x" };
            Assert.False(Balance(service).AllFree);
        }

        [Fact]
        public void AServiceFromBeforeFreeFacesGivesPlainCredits()
        {
            using var service = new StandIn { BalanceAnswer = () => new { email = "player@example.com", credits = 40, buy_url = "x" } };
            var account = Balance(service);
            Assert.Equal((40, true, 0, false), (account.Credits, account.Bought, account.Free, account.AllFree));
            Assert.Equal("You have 40 credits left.", Credits.LeftText(account));
        }

        [Fact]
        public void ABalanceThatIsntOneSaysWhy()
        {
            using var service = new StandIn();
            Assert.Equal(ImageApi.SignedOut, Assert.Throws<ApiKeyRejectedException>(() => Balance(service, "rfm_" + new string('x', 43))).Message);
            service.BalanceAnswer = () => new { email = "player@example.com", credits = "lots" };
            Assert.Equal("RegenerateFM credits sent an unexpected answer.", Assert.Throws<InvalidOperationException>(() => Balance(service)).Message);
            service.Status = _ => 500;
            Assert.Equal("RegenerateFM credits answered HTTP 500.", Assert.Throws<InvalidOperationException>(() => Balance(service)).Message);
        }

        [Fact]
        public void AFaceMadeWithCreditsSaysWhatsLeft()
        {
            using var service = new StandIn();
            var settings = new ImageSettings { ApiKey = service.Token, BaseUrl = Credits.ApiBase(service.Url) };
            // A service from before these headers says nothing.
            ImageApi.GeneratePng(settings, "A face").GetAwaiter().GetResult();
            Assert.Null(settings.CreditsLeft);

            service.ImageHeaders["x-regeneratefm-credits"] = "74";
            ImageApi.GeneratePng(settings, "A face").GetAwaiter().GetResult();
            Assert.Equal((74, true, false), (settings.CreditsLeft.Credits, settings.CreditsLeft.Bought, settings.CreditsLeft.AllFree));
            Assert.Equal("You have 74 credits left.", Credits.LeftText(settings.CreditsLeft));

            // While every credit left is a free one, the service says so too.
            service.ImageHeaders["x-regeneratefm-credits"] = "2";
            service.ImageHeaders["x-regeneratefm-free"] = "2";
            ImageApi.EditPng(settings, "Update", ImageApi.FitPortrait(Pngs.Make(512))).GetAwaiter().GetResult();
            Assert.True(settings.CreditsLeft.AllFree);
            Assert.Equal("You have 2 free faces left.", Credits.LeftText(settings.CreditsLeft));

            // An OpenAI key's answer is OpenAI's, whatever its headers.
            var openAi = new ImageSettings { ApiKey = "sk-test", BaseUrl = service.Url + "/v1" };
            ImageApi.GeneratePng(openAi, "A face").GetAwaiter().GetResult();
            Assert.Null(openAi.CreditsLeft);
        }

        [Fact]
        public void RunningOutSaysWhetherTheCreditsWereTheFreeOnes()
        {
            using var service = new StandIn
            {
                OutOfCredits = new
                {
                    message = "You've used your 3 free faces. Packs start at 75 faces for £2. Buy more at https://credits.test/link",
                    type = "free_credits_used", code = "free_credits_used", free_credits = 3,
                },
            };
            var settings = new ImageSettings { ApiKey = service.Token, BaseUrl = Credits.ApiBase(service.Url) };
            var free = Assert.Throws<OutOfCreditsException>(() => ImageApi.GeneratePng(settings, "A face").GetAwaiter().GetResult());
            Assert.Equal((true, 3), (free.Free, free.FreeFaces));
            Assert.StartsWith("You've used your 3 free faces.", free.Message);
            Assert.Equal("You've used your 3 free faces. Packs start at 75 faces for £2.", free.RanOutText);

            service.OutOfCredits = new
            {
                message = "You have no RegenerateFM credits left. Buy more at https://credits.test/link",
                type = "insufficient_credits", code = "insufficient_credits",
            };
            var bought = Assert.Throws<OutOfCreditsException>(() => ImageApi.GeneratePng(settings, "A face").GetAwaiter().GetResult());
            Assert.Equal((false, 0), (bought.Free, bought.FreeFaces));
            Assert.Equal("You've used all your RegenerateFM credits.", bought.RanOutText);
        }

        [Fact]
        public void CreditsAreWordedForOneOrMany()
        {
            Credits.Account Left(int credits, bool allFree) =>
                new Credits.Account { Credits = credits, Bought = !allFree, Free = allFree ? credits : 0 };
            Assert.Equal(new[]
            {
                "You have 1 free face left.", "You have 3 free faces left.", "You have 0 free faces left.",
                "You have 1 credit left.", "You have 12 credits left.",
            }, new[]
            {
                Credits.LeftText(Left(1, true)), Credits.LeftText(Left(3, true)), Credits.LeftText(Left(0, true)),
                Credits.LeftText(Left(1, false)), Credits.LeftText(Left(12, false)),
            });
            Assert.Equal("You've used your 1 free face. Packs start at 75 faces for £2.", Credits.FreeUsedText(1));
            Assert.Equal("You've used your 3 free faces. Packs start at 75 faces for £2.", Credits.FreeUsedText(3));
            Assert.Equal("You have 3 credits; this needs 20.", Credits.ShortText(3, 20));
            Assert.Equal("You have 1 credit; this needs 20.", Credits.ShortText(1, 20));
        }

        [Fact]
        public void TheNewWordingIsTranslated()
        {
            string language = Text.Language;
            try
            {
                Text.Language = "german";
                Assert.Equal("Sie haben noch 2 kostenlose Gesichter.", Credits.LeftText(new Credits.Account { Credits = 2, Bought = false, Free = 2 }));
                Assert.Equal("Sie haben Ihre 3 kostenlosen Gesichter verbraucht. Pakete gibt es ab 75 Gesichtern für £2.", Credits.FreeUsedText(3));
                Text.Language = "spanish (latam)";
                Assert.Equal("Tienes 3 créditos; esto necesita 20.", Credits.ShortText(3, 20));
                Text.Language = "japanese";
                Assert.Equal("残りは12クレジットです。", Credits.LeftText(new Credits.Account { Credits = 12 }));
            }
            finally
            {
                Text.Language = language;
            }
        }
    }
}
