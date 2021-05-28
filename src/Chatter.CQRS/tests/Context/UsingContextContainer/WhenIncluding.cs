using Chatter.CQRS.Context;
using Moq;
using Xunit;

namespace Chatter.CQRS.Tests.Context.UsingContextContainer
{
    public class ContextToInclude { }

    public class WhenIncluding
    {
        public readonly ContextContainer _sut;

        public WhenIncluding()
        {
            _sut = new ContextContainer();
        }

        //public void Include<T>(T t)
        //    => Include(typeof(T).FullName, t);

        //public void Include<T>(string fullQualifiedNamespaceOfType, T t)
        //    => _context[fullQualifiedNamespaceOfType] = t;


        [Fact]
        public void MustContainContextAfterInclude()
        {
            var c1 = new Mock<FakeContext>();
            _sut.Include(c1.Object);
            var c1ByType = _sut.Get<FakeContext>();
            var c1ByNamespace = _sut.Get<FakeContext>(typeof(FakeContext).FullName);
            Assert.Equal(c1.Object, c1ByType);
            Assert.Equal(c1.Object, c1ByNamespace);
        }

        [Fact]
        public void MustContainMostRecentlyAddedContextForSameType()
        {
            var c1 = new Mock<FakeContext>();
            var c2 = new Mock<FakeContext>();
            _sut.Include(c1.Object);
            var c1ByType = _sut.Get<FakeContext>();
            Assert.Equal(c1.Object, c1ByType);
            _sut.Include(c2.Object);
            var c2ByType = _sut.Get<FakeContext>();
            Assert.NotEqual(c1.Object, c2ByType);
            Assert.Equal(c2.Object, c2ByType);
        }

        [Fact]
        public void MustContainMostRecentlyAddedContextForSameKey()
        {
            var c1 = "fake value 1";
            var c2 = "fake value 2";
            _sut.Include("Key", c1);
            var c1ByType = _sut.Get<string>("Key");
            Assert.Equal(c1, c1ByType);
            _sut.Include("Key", c2);
            var c2ByType = _sut.Get<string>("Key");
            Assert.NotEqual(c1, c2ByType);
            Assert.Equal(c2, c2ByType);
        }
    }
}
