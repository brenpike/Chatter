using Chatter.CQRS.Context;
using Moq;
using Xunit;

namespace Chatter.CQRS.Tests.Context.UsingContextContainer
{
    public class WhenGettingOrAdding
    {
        private readonly ContextContainer _parentContextContainer;
        private readonly ContextContainer _sut;
        private readonly Mock<ReallyFakeContext> _reallyFakeContext;
        private readonly Mock<FakeContext> _fakeContext;

        public WhenGettingOrAdding()
        {
            _parentContextContainer = new ContextContainer();
            _reallyFakeContext = new Mock<ReallyFakeContext>();
            _parentContextContainer.Include(_reallyFakeContext.Object);
            _sut = new ContextContainer(_parentContextContainer);
            _fakeContext = new Mock<FakeContext>();
            _sut.Include(_fakeContext.Object);
        }

        [Fact]
        public void MustReturnFactoryMethodResultWhenTypeDoesntExist()
        {
            var newContext = new AnotherFakeContext();
            var doesExistBefore = _sut.TryGet<AnotherFakeContext>(out var ctx);
            Assert.False(doesExistBefore);
            Assert.Null(ctx);
            var c1New = _sut.GetOrAdd(() => newContext);
            var doesExistAfter = _sut.TryGet<AnotherFakeContext>(out var ctx2);
            Assert.Equal(newContext, c1New);
            Assert.Same(newContext, c1New);
            Assert.Equal(newContext, ctx2);
            Assert.True(doesExistAfter);
        }

        [Fact]
        public void MustGetExistingContextWhenTypeAlreadyExists()
        {
            var fakeContext2 = new FakeContext();
            var c1 = _sut.GetOrAdd(() => fakeContext2);
            Assert.Equal(_fakeContext.Object, c1);
            Assert.Same(_fakeContext.Object, c1);
            Assert.NotEqual(fakeContext2, c1);
        }

        [Fact]
        public void MustGetExistingContextWhenTypeAlreadyExistsInInheritedContextContainer()
        {
            var fakeContext2 = new ReallyFakeContext();
            var c1 = _sut.GetOrAdd(() => fakeContext2);
            Assert.Equal(_reallyFakeContext.Object, c1);
            Assert.Same(_reallyFakeContext.Object, c1);
            Assert.NotEqual(fakeContext2, c1);
        }
    }
}
