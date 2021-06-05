using Chatter.CQRS.Context;
using Moq;
using Xunit;

namespace Chatter.CQRS.Tests.Context.UsingContextContainer
{
    public class WhenGettingOrNew
    {
        private readonly ContextContainer _parentContextContainer;
        private readonly ContextContainer _sut;
        private readonly Mock<ReallyFakeContext> _reallyFakeContext;
        private readonly Mock<FakeContext> _fakeContext;

        public WhenGettingOrNew()
        {
            _parentContextContainer = new ContextContainer();
            _reallyFakeContext = new Mock<ReallyFakeContext>();
            _parentContextContainer.Include(_reallyFakeContext.Object);
            _sut = new ContextContainer(_parentContextContainer);
            _fakeContext = new Mock<FakeContext>();
            _sut.Include(_fakeContext.Object);
        }

        [Fact]
        public void MustReturnNewOfTWhenTypeDoesntExist()
        {
            var doesExistBefore = _sut.TryGet(out AnotherFakeContext newContext);
            Assert.False(doesExistBefore);
            Assert.Null(newContext);
            var c1New = _sut.GetOrNew<AnotherFakeContext>();
            var doesExistAfter = _sut.TryGet(out newContext);
            Assert.Equal(newContext, c1New);
            Assert.True(doesExistAfter);
        }

        [Fact]
        public void MustGetExistingContextWhenTypeAlreadyExists()
        {
            var c1 = _sut.GetOrNew<FakeContext>();
            Assert.Equal(_fakeContext.Object, c1);
        }

        [Fact]
        public void MustGetExistingContextWhenTypeAlreadyExistsInInheritedContextContainer()
        {
            var c1 = _sut.GetOrNew<ReallyFakeContext>();
            Assert.Equal(_reallyFakeContext.Object, c1);
        }
    }
}
