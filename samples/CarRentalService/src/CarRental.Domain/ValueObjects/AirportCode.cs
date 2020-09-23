using Samples.SharedKernel;
using System;
using System.Collections.Generic;
using System.Text;

namespace CarRental.Domain.ValueObjects
{
    public class AirportCode : ValueObject
    {
        public string Code { get; }

        public AirportCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new BusinessRuleException($"An airport code is required");
            }

            if (code.Length != 3)
            {
                throw new BusinessRuleException("An IATA airport code must be 3 characters long.");
            }

            Code = code;
        }

        protected override IEnumerable<object> GetAtomicValues()
        {
            yield return Code;
        }
    }
}
