using CarRental.Application.DTO;
using Chatter.CQRS.Context;
using Chatter.CQRS.Queries;
using Samples.SharedKernel.Interfaces;
using System;
using System.Threading.Tasks;

namespace CarRental.Application.Queries
{
    public class GetCarRentalHandler : IQueryHandler<GetCarRental, CarRentalDto>
    {
        private readonly IRepository<Domain.Aggregates.CarRental, Guid> _repository;

        public GetCarRentalHandler(IRepository<Domain.Aggregates.CarRental, Guid> repository) 
            => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

        public async Task<CarRentalDto> Handle(GetCarRental query, IMessageHandlerContext context)
        {
            var rental = await _repository.GetByIdAsync(query.Id);
            var rentalDto = new CarRentalDto
            {
                Id = rental.Id,
                Airport = rental.Airport,
                From = rental.From,
                Until = rental.Until,
                ReservationId = rental.ReservationId,
                Vendor = rental.Vendor
            };

            return rentalDto;
        }
    }
}
