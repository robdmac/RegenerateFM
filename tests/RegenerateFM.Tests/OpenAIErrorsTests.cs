using System;
using Xunit;

namespace RegenerateFM.Tests
{
    // An OpenAI key whose account has no credit left: the player is told so,
    // and where to add it, rather than seeing only that a face failed.
    public class OpenAIErrorsTests
    {
        private static Exception Refused(int status, string code, string type, string message, string key = "sk-test-key-0123456789abcdef")
        {
            using var api = new StandIn
            {
                Status = path => path.EndsWith("/images/generations") ? status : null,
                ErrorBody = _ => new { error = new { message, type, code } },
            };
            return Assert.ThrowsAny<InvalidOperationException>(() =>
                ImageApi.GeneratePng(new ImageSettings { ApiKey = key, BaseUrl = api.Url + "/v1" }, "A face").GetAwaiter().GetResult());
        }

        [Fact]
        public void AnAccountOutOfQuotaSaysWhereToAddCredit()
        {
            // What OpenAI sends when the account's prepaid credit is used up.
            var error = Assert.IsType<OpenAIBillingException>(Refused(429, "insufficient_quota", "insufficient_quota",
                "You exceeded your current quota, please check your plan and billing details. For more information on this error, read the docs: https://platform.openai.com/docs/guides/error-codes/api-errors."));
            Assert.StartsWith("Your OpenAI account has run out of credit. Add credit in your OpenAI account's billing settings (platform.openai.com), " +
                              "or use RegenerateFM credits instead.", error.Message);
            Assert.StartsWith("OpenAI image API returned HTTP 429: You exceeded your current quota", error.Detail);
            Assert.Contains(error.Detail, error.Message);
        }

        [Fact]
        public void ASpendingLimitReachedIsTheSame()
        {
            Assert.IsType<OpenAIBillingException>(Refused(400, "billing_hard_limit_reached", "invalid_request_error",
                "Billing hard limit has been reached."));
        }

        // A busy API isn't an account out of credit.
        [Fact]
        public void ARateLimitIsAnOrdinaryFailure()
        {
            var error = Refused(429, "rate_limit_exceeded", "requests", "Rate limit reached for gpt-image-1.");
            Assert.IsNotType<OpenAIBillingException>(error);
            Assert.Equal("OpenAI image API returned HTTP 429: Rate limit reached for gpt-image-1.", error.Message);
        }

        // RegenerateFM credits have their own words (OutOfCreditsException).
        [Fact]
        public void ACreditsTokenIsntAnOpenAIAccount()
        {
            Assert.IsNotType<OpenAIBillingException>(Refused(429, "insufficient_quota", "insufficient_quota", "Busy.", "rfm_" + new string('t', 43)));
        }
    }
}
