using Chatter.CQRS.Context;
using Moq;
using Xunit;

namespace Chatter.CQRS.Tests.Context.UsingContextContainer
{
    public class WhenTryingToGet
    {
        private readonly ContextContainer _parentContextContainer;
        private readonly ContextContainer _sut;
        private readonly Mock<ReallyFakeContext> _reallyFakeContext;
        private readonly string _anotherFakeContext = "fake value";
        private readonly Mock<FakeContext> _fakeContext;

        public WhenTryingToGet()
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
        public void MustReturnFalseAndDefaultOutParameterWhenFullQualifiedNamespaceOfTypeDoesntExist()
        {
            var c1Failure = _sut.TryGet<int>("not.a.real.namespace", out var c1);
            Assert.False(c1Failure);
            Assert.Equal(default, c1);
        }

        [Fact]
        public void MustReturnFalseAndDefaultOutParameterWhenTypeDoesntExist()
        {
            var c1Failure = _sut.TryGet<int>(out var c1);
            Assert.False(c1Failure);
            Assert.Equal(default, c1);
        }

        [Fact]
        public void MustReturnTrueAndContextViaOutParameterWhenFullQualifiedNamespaceOfTypeExists()
        {
            var c1Success = _sut.TryGet<FakeContext>(typeof(FakeContext).FullName, out var c1);
            Assert.Equal(_fakeContext.Object, c1);
            Assert.Same(_fakeContext.Object, c1);
            Assert.True(c1Success);
        }

        [Fact]
        public void MustReturnTrueAndContextViaOutParameterWhenTypeExists()
        {
            var c1Success = _sut.TryGet<FakeContext>(out var c1);
            Assert.Equal(_fakeContext.Object, c1);
            Assert.Same(_fakeContext.Object, c1);
            Assert.True(c1Success);
        }

        [Fact]
        public void MustReturnTrueAndContextViaOutParameterWhenTypeExistsInInheritedContextContainer()
        {
            var c1Success = _sut.TryGet<ReallyFakeContext>(out var c1);
            Assert.Equal(_reallyFakeContext.Object, c1);
            Assert.Same(_reallyFakeContext.Object, c1);
            Assert.True(c1Success);
        }

        [Fact]
        public void MustReturnTrueAndContextViaOutParameterWhenFullQualifiedNamespaceOfTypeExistsInInheritedContextContainer()
        {
            var c1Success = _sut.TryGet<ReallyFakeContext>(typeof(ReallyFakeContext).FullName, out var c1);
            var c2Success = _sut.TryGet<string>("Key", out var c2);
            Assert.Equal(_reallyFakeContext.Object, c1);
            Assert.Same(_reallyFakeContext.Object, c1);
            Assert.Equal(_anotherFakeContext, c2);
            Assert.Same(_anotherFakeContext, c2);
            Assert.True(c1Success);
            Assert.True(c2Success);
        }
    }
}
