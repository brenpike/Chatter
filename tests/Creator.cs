namespace Chatter.Testing.Core
{
    /// <summary>
    /// This is a base creator class providing the ability to set up the state of the object
    /// </summary>
    public class Creator<T>
    {
        protected INewContext New { get; }
        public T Creation { get; protected set; }
        public Creator(INewContext newContext, T creation = default)
        {
            New = newContext;
            Creation = creation;
        }

        /// <summary>
        /// Implicitly converts the creator to instance of the creation
        /// </summary>
        /// <param name="creator"></param>
        public static implicit operator T(Creator<T> creator)
        {
            return creator.Creation;
        }
    }
}
