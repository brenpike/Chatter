using Chatter.CQRS.Context;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace Chatter.CQRS.Tests.Context.UsingContextContainer
{

    public class WhenGetting
    {
        private readonly ContextContainer _parentContextContainer;
        private readonly ContextContainer _sut;
        private readonly Mock<ReallyFakeContext> _reallyFakeContext;
        private readonly string _anotherFakeContext = "fake value";
        private readonly Mock<FakeContext> _fakeContext;

        public WhenGetting()
        {
            _parentContextContainer = new ContextContainer();
            _reallyFakeContext = new Mock<ReallyFakeContext>();
            _parentContextContainer.Include("Key", _anotherFakeContext);
            _parentContextContainer.Include(_reallyFakeContext.Object);
            _sut = new ContextContainer(_parentContextContainer);
            _fakeContext = new Mock<FakeContext>();
            _sut.Include(_fakeContext.Object);
        }

        [Fact]
        public void MustThrowExceptionWhenFullQualifiedNamespaceOfTypeDoesntExist()
            => FluentActions.Invoking(() => _sut.Get<int>("not.a.real.namespace")).Should().Throw<KeyNotFoundException>();

        [Fact]
        public void MustThrowExceptionWhenTypeDoesntExist()
            => FluentActions.Invoking(() => _sut.Get<int>()).Should().Throw<KeyNotFoundException>();

        [Fact]
        public void MustGetContextWhenFullQualifiedNamespaceOfTypeExists()
        {
            var c1 = _sut.Get<FakeContext>(typeof(FakeContext).FullName);
            Assert.Equal(_fakeContext.Object, c1);
        }

        [Fact]
        public void MustGetContextWhenTypeExists()
        {
            var c1 = _sut.Get<FakeContext>();
            Assert.Equal(_fakeContext.Object, c1);
        }

        [Fact]
        public void MustGetContextWhenTypeExistsInInheritedContextContainer()
        {
            var c1 = _sut.Get<ReallyFakeContext>();
            Assert.Equal(_reallyFakeContext.Object, c1);
        }

        [Fact]
        public void MustGetContextWhenFullQualifiedNamespaceOfTypeExistsInInheritedContextContainer()
        {
            var c1 = _sut.Get<ReallyFakeContext>(typeof(ReallyFakeContext).FullName);
            var c2 = _sut.Get<string>("Key");
            Assert.Equal(_reallyFakeContext.Object, c1);
            Assert.Equal(_anotherFakeContext, c2);
        }
    }

    public class FakeContext { }
    public class ReallyFakeContext { }
}
