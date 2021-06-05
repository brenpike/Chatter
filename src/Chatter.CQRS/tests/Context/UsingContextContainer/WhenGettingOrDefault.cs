using Chatter.CQRS.Context;
using Moq;
using Xunit;

namespace Chatter.CQRS.Tests.Context.UsingContextContainer
{
    public class WhenGettingOrDefault
    {
        private readonly ContextContainer _parentContextContainer;
        private readonly ContextContainer _sut;
        private readonly Mock<ReallyFakeContext> _reallyFakeContext;
        private readonly Mock<FakeContext> _fakeContext;

        public WhenGettingOrDefault()
        {
            _parentContextContainer = new ContextContainer();
            _reallyFakeContext = new Mock<ReallyFakeContext>();
            _parentContextContainer.Include(_reallyFakeContext.Object);
            _sut = new ContextContainer(_parentContextContainer);
            _fakeContext = new Mock<FakeContext>();
            _sut.Include(_fakeContext.Object);
        }

        [Fact]
        public void MustReturnDefaultWhenTypeDoesntExist()
        {
            var doesExistBefore = _sut.TryGet(out AnotherFakeContext newContext);
            Assert.False(doesExistBefore);
            Assert.Null(newContext);
            var c1New = _sut.GetOrDefault<AnotherFakeContext>();
            var doesExistAfter = _sut.TryGet(out newContext);
            Assert.Equal(default, c1New);
            Assert.Equal(newContext, c1New);
            Assert.True(doesExistAfter);
        }

        [Fact]
        public void MustGetExistingContextWhenTypeAlreadyExists()
        {
            var fakeContext2 = default(FakeContext);
            var c1 = _sut.GetOrDefault<FakeContext>();
            Assert.Equal(_fakeContext.Object, c1);
            Assert.Same(_fakeContext.Object, c1);
            Assert.NotEqual(fakeContext2, c1);
        }

        [Fact]
        public void MustGetExistingContextWhenTypeAlreadyExistsInInheritedContextContainer()
        {
            var fakeContext2 = default(ReallyFakeContext);
            var c1 = _sut.GetOrDefault<ReallyFakeContext>();
            Assert.Equal(_reallyFakeContext.Object, c1);
            Assert.Same(_reallyFakeContext.Object, c1);
            Assert.NotEqual(fakeContext2, c1);
        }
    }
}
