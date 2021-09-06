using Moq;
using System;
using System.Reflection;

namespace Chatter.Testing.Core.Creators.Common
{
    public class AssemblyCreator : Creator<Assembly>
    {
        private readonly Mock<Assembly> _assemblyMock = new Mock<Assembly>();

        public AssemblyCreator(INewContext newContext, Assembly assembly = null)
            : base(newContext, assembly)
        {
            WithFullName(Guid.NewGuid().ToString());
            WithTypes(New.Common().Type.Creation);
            _assemblyMock.Setup(a => a.Equals(It.IsAny<Assembly>()))
                         .Returns<Assembly>(x => x.FullName == _assemblyMock.Object.FullName
                                                 && x.GetTypes() == _assemblyMock.Object.GetTypes());

            Creation = _assemblyMock.Object;
        }

        public AssemblyCreator WithTypes(params Type[] types)
        {
            _assemblyMock.Setup(a => a.GetTypes()).Returns(types);
            return this;
        }

        public AssemblyCreator WithFullName(string fullName)
        {
            _assemblyMock.SetupGet(a => a.FullName).Returns(fullName);
            return this;
        }
    }
}
