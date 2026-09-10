using Chatter.CQRS.Context;
using Moq;
using System;
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

        [Fact]
        public void MustStoreFactoryMethodResultWhenValueTypeDoesntExist()
        {
            var doesExistBefore = _sut.TryGet<int>(out _);
            var c1New = _sut.GetOrAdd(() => 42);
            var doesExistAfter = _sut.TryGet<int>(out var ctx);
            Assert.False(doesExistBefore);
            Assert.Equal(42, c1New);
            Assert.True(doesExistAfter);
            Assert.Equal(42, ctx);
        }

        [Fact]
        public void MustNotInvokeFactoryMethodASecondTimeWhenValueTypeAlreadyExists()
        {
            var invocationCount = 0;
            var c1New = _sut.GetOrAdd(() => { invocationCount++; return 42; });
            var c1 = _sut.GetOrAdd(() => { invocationCount++; return 99; });
            Assert.Equal(42, c1New);
            Assert.Equal(42, c1);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public void MustNotInvokeFactoryMethodASecondTimeWhenStructAlreadyExists()
        {
            var expected = Guid.NewGuid();
            var invocationCount = 0;
            var c1New = _sut.GetOrAdd(() => { invocationCount++; return expected; });
            var c1 = _sut.GetOrAdd(() => { invocationCount++; return Guid.NewGuid(); });
            Assert.Equal(expected, c1New);
            Assert.Equal(expected, c1);
            Assert.Equal(1, invocationCount);
        }

        [Fact]
        public void MustNotInvokeFactoryMethodWhenNullIsStoredForType()
        {
            _sut.Include<AnotherFakeContext>(null);
            var invocationCount = 0;
            var c1 = _sut.GetOrAdd(() => { invocationCount++; return new AnotherFakeContext(); });
            Assert.Null(c1);
            Assert.Equal(0, invocationCount);
        }

        [Fact]
        public void MustInvokeNullProducingFactoryMethodExactlyOnce()
        {
            var invocationCount = 0;
            var c1New = _sut.GetOrAdd<AnotherFakeContext>(() => { invocationCount++; return null; });
            var c1 = _sut.GetOrAdd<AnotherFakeContext>(() => { invocationCount++; return null; });
            Assert.Null(c1New);
            Assert.Null(c1);
            Assert.Equal(1, invocationCount);
        }
    }
}
