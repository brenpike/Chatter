using Chatter.CQRS.DependencyInjection;
using Moq;
using System.Reflection;

namespace Chatter.Testing.Core.Creators.CQRS
{
    public class AssemblyFilterSourceProviderCreator : Creator<IAssemblyFilterSourceProvider>
    {
        private readonly Mock<IAssemblyFilterSourceProvider> _assemblyFilterSourceProviderMock = new Mock<IAssemblyFilterSourceProvider>();

        public AssemblyFilterSourceProviderCreator(INewContext newContext, IAssemblyFilterSourceProvider creation = null)
            : base(newContext, creation)
        {
            Creation = _assemblyFilterSourceProviderMock.Object;
        }

        public AssemblyFilterSourceProviderCreator WithSourceAssemblies(params Assembly[] assemblies)
        {
            _assemblyFilterSourceProviderMock.Setup(asp => asp.GetSourceAssemblies()).Returns(assemblies);
            return this;
        }
    }
}
