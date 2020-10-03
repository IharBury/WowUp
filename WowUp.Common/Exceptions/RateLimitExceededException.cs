using System;

namespace WowUp.Common.Exceptions
{
    public class RateLimitExceededException : Exception
    {
        public RateLimitExceededException(Exception innerException = null)
            : base(null, innerException)
        {
        }
    }
}
