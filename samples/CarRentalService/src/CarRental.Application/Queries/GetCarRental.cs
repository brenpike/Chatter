using CarRental.Application.DTO;
using Chatter.CQRS.Queries;
using System;

namespace CarRental.Application.Queries
{
    public class GetCarRental : IQuery<CarRentalDto>
    {
        public Guid Id { get; set; }
    }
}
