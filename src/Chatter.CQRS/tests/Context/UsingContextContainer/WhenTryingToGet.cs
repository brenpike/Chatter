using Chatter.CQRS.Context;
using Moq;
using System;
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
        public void MustReturnFalseAndDefaultOutParameterWhenStoredValueIsNotAssignableToRequestedType()
        {
            _sut.Include("MismatchKey", "not an int");
            var c1Failure = _sut.TryGet<int>("MismatchKey", out var c1);
            Assert.False(c1Failure);
            Assert.Equal(0, c1);
        }

        [Fact]
        public void MustReturnFalseAndDefaultOutParameterWhenStoredNullIsReadAsNonNullableValueType()
        {
            _sut.Include<object>("NullKey", null);
            var c1Failure = _sut.TryGet<Guid>("NullKey", out var c1);
            Assert.False(c1Failure);
            Assert.Equal(default, c1);
        }

        [Fact]
        public void MustReturnTrueAndNullOutParameterWhenStoredNullIsReadAsReferenceType()
        {
            _sut.Include<object>("NullKey", null);
            var c1Success = _sut.TryGet<string>("NullKey", out var c1);
            Assert.True(c1Success);
            Assert.Null(c1);
        }

        [Fact]
        public void MustReturnTrueAndNullOutParameterWhenStoredNullIsReadAsNullableValueType()
        {
            _sut.Include<object>("NullKey", null);
            var c1Success = _sut.TryGet<int?>("NullKey", out var c1);
            Assert.True(c1Success);
            Assert.Null(c1);
        }

        [Fact]
        public void MustReturnTrueAndValueOutParameterWhenStoredValueTypeIsReadAsNullableValueType()
        {
            _sut.Include("NumberKey", 5);
            var c1Success = _sut.TryGet<int?>("NumberKey", out var c1);
            Assert.True(c1Success);
            Assert.Equal(5, c1);
        }

        [Fact]
        public void MustReturnTrueAndContextViaOutParameterWhenStoredValueIsReadAsBaseTypeOrInterface()
        {
            var derived = new DerivedFakeContext();
            _sut.Include("DerivedKey", derived);
            var c1Success = _sut.TryGet<FakeContext>("DerivedKey", out var c1);
            var c2Success = _sut.TryGet<IFakeContextMarker>("DerivedKey", out var c2);
            Assert.True(c1Success);
            Assert.Same(derived, c1);
            Assert.True(c2Success);
            Assert.Same(derived, c2);
        }

        [Fact]
        public void MustReturnFalseWhenLocallyStoredValueOfAnotherTypeShadowsInheritedContext()
        {
            _sut.Include("Key", 5);
            var c1Failure = _sut.TryGet<string>("Key", out var c1);
            Assert.False(c1Failure);
            Assert.Null(c1);
        }

        [Fact]
        public void MustReturnFalseWhenInheritedContextValueIsNotAssignableToRequestedType()
        {
            var c1Failure = _sut.TryGet<int>("Key", out var c1);
            Assert.False(c1Failure);
            Assert.Equal(0, c1);
        }

        [Fact]
        public void MustReturnTrueAndEqualValueForAStringKeyedStructWrittenByTheOutboxPath()
        {
            // Pins the in-repo pair: UnitOfWork.cs:35 writes Include("CurrentTransactionId", Guid) and
            // OutboxProcessingBehavior.cs:28 reads TryGet<Guid>("CurrentTransactionId", out _).
            var transactionId = Guid.NewGuid();
            _sut.Include("CurrentTransactionId", transactionId);
            var c1Success = _sut.TryGet<Guid>("CurrentTransactionId", out var c1);
            Assert.True(c1Success);
            Assert.Equal(transactionId, c1);
        }

        [Fact]
        public void MustRoundTripReferenceValueAndStoredNullContextUnderTheTypeKey()
        {
            var anotherFakeContext = new AnotherFakeContext();
            _sut.Include(anotherFakeContext);
            _sut.Include(42);
            var referenceSuccess = _sut.TryGet<AnotherFakeContext>(out var reference);
            var valueSuccess = _sut.TryGet<int>(out var value);
            _sut.Include<AnotherFakeContext>(null);
            var storedNullSuccess = _sut.TryGet<AnotherFakeContext>(out var storedNull);
            Assert.True(referenceSuccess);
            Assert.Same(anotherFakeContext, reference);
            Assert.True(valueSuccess);
            Assert.Equal(42, value);
            Assert.True(storedNullSuccess);
            Assert.Null(storedNull);
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

    public interface IFakeContextMarker { }

    public class DerivedFakeContext : FakeContext, IFakeContextMarker { }
}
