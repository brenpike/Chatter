using Chatter.CQRS.Commands;
using cr = Samples.SharedKernel.Dtos;

namespace CarRental.Application.Commands
{
    public class BookRentalCarCommand : ICommand
    {
        public cr.CarRental Car { get; set; }
    }
}
