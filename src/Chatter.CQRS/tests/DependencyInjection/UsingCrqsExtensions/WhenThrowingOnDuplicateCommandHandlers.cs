using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenThrowingOnDuplicateCommandHandlers : Testing.Core.Context
    {
        [Fact]
        public void MustThrowNamingTheCommandAndEveryCompetingHandler()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().Throw<InvalidOperationException>()
                         .Which.Message.Should().Contain(typeof(FakeCommand).FullName)
                         .And.Contain(typeof(FakeFirstCommandHandler).FullName)
                         .And.Contain(typeof(FakeSecondCommandHandler).FullName);
        }

        [Fact]
        public void MustNotThrowWhenSeveralHandlersHandleTheSameEvent()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeEventHandler), typeof(FakeEventHandler2));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowWhenTheOnlyOtherHandlersOfACommandAreAbstractOrOpenGeneric()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeAbstractCommandHandler),
                                                        typeof(FakeConcreteCommandHandler),
                                                        typeof(FakeOpenGenericCommandHandler<>));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustReportEveryAmbiguousCommandInOneExceptionOrderedByFullName()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeSecondGenericCommandHandler),
                                                        typeof(FakeSecondCommandHandler),
                                                        typeof(FakeFirstGenericCommandHandler),
                                                        typeof(FakeFirstCommandHandler));

            var message = FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers())
                                       .Should().Throw<InvalidOperationException>()
                                       .Which.Message;

            message.Should().Contain(typeof(FakeCommand).FullName)
                   .And.Contain(typeof(FakeFirstCommandHandler).FullName)
                   .And.Contain(typeof(FakeSecondCommandHandler).FullName)
                   .And.Contain(typeof(FakeGenericCommand<string>).FullName)
                   .And.Contain(typeof(FakeFirstGenericCommandHandler).FullName)
                   .And.Contain(typeof(FakeSecondGenericCommandHandler).FullName);

            PositionOf(message, typeof(FakeCommand)).Should().BeLessThan(PositionOf(message, typeof(FakeGenericCommand<string>)));
            PositionOf(message, typeof(FakeFirstCommandHandler)).Should().BeLessThan(PositionOf(message, typeof(FakeSecondCommandHandler)));
            PositionOf(message, typeof(FakeFirstGenericCommandHandler)).Should().BeLessThan(PositionOf(message, typeof(FakeSecondGenericCommandHandler)));
        }

        [Fact]
        public void MustRegisterTheLastHandlerScannedWithoutThrowingWhenTheCheckIsNotEnabled()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler)).Creation;
            var services = new ServiceCollection();

            FluentActions.Invoking(() => services.AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                                 messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly)))
                         .Should().NotThrow();

            services.Should().ContainSingle(sd => sd.ServiceType == typeof(IMessageHandler<FakeCommand>))
                    .Which.ImplementationType.Should().Be(typeof(FakeSecondCommandHandler));
        }

        [Fact]
        public void MustReturnTheSameBuilderWhenNoCommandIsHandledMoreThanOnce()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeFirstCommandHandler));

            chatterBuilder.ThrowOnDuplicateCommandHandlers().Should().BeSameAs(chatterBuilder);
        }

        [Fact]
        public void MustNotThrowWhenHandlersHandleDifferentCommands()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeFirstCommandHandler), typeof(FakeFirstGenericCommandHandler));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowForAHandlerOfBothACommandAndAnEvent()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeDualRoleHandler));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustNotReportCompetingHandlersInAnAssemblyOutsideTheFilterSet()
        {
            var scannedAssembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler)).Creation;
            var unscannedAssembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler)).Creation;
            var chatterBuilder = new ServiceCollection().AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                                        messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(scannedAssembly));

            chatterBuilder.AssemblySourceFilter.Apply().Should().NotContain(unscannedAssembly);
            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustNotReportACompilerGeneratedHandlerBecauseItIsNeverRegistered()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler), typeof(FakeCompilerGeneratedCommandHandler)).Creation;
            var services = new ServiceCollection();
            var chatterBuilder = services.AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                          messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly));

            services.Should().NotContain(sd => sd.ImplementationType == typeof(FakeCompilerGeneratedCommandHandler));
            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        /// <summary>
        /// Characterization pin, not a red-first test: the handler scan uses Scrutor's
        /// <c>AddClasses(Action&lt;IImplementationTypeFilter&gt;, bool)</c> overload, passing
        /// <c>publicOnly: false</c> explicitly (see the INVARIANT at
        /// ServiceCollectionExtensions.RegisterBehaviorForAllCommands), so non-public handlers are
        /// registered and therefore reported.
        /// Locks that ground truth so a Scrutor upgrade breaks this test instead of silently
        /// narrowing both the registration and the check.
        /// </summary>
        [Fact]
        public void MustRegisterAndReportNonPublicCompetingHandlers()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler)).Creation;
            var services = new ServiceCollection();
            var chatterBuilder = services.AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                          messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly));

            typeof(FakeFirstCommandHandler).IsVisible.Should().BeFalse();
            services.Should().ContainSingle(sd => sd.ServiceType == typeof(IMessageHandler<FakeCommand>))
                    .Which.ImplementationType.Should().Be(typeof(FakeSecondCommandHandler));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().Throw<InvalidOperationException>()
                         .Which.Message.Should().Contain(typeof(FakeFirstCommandHandler).FullName)
                         .And.Contain(typeof(FakeSecondCommandHandler).FullName);
        }

        [Fact]
        public void MustNotReportOneHandlerReachableFromTwoScannedAssemblies()
        {
            var firstAssembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler)).Creation;
            var secondAssembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler)).Creation;
            var chatterBuilder = new ServiceCollection().AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                                        messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(firstAssembly, secondAssembly));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustLeaveTheApplicationServiceCollectionUntouched()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler)).Creation;
            var services = new ServiceCollection();
            var chatterBuilder = services.AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                          messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly));
            var descriptorsBeforeCheck = services.ToList();

            chatterBuilder.ThrowOnDuplicateCommandHandlers();

            services.Should().Equal(descriptorsBeforeCheck);
        }

        [Fact]
        public void MustDescribeTheScanOrderTheScanActuallyUses()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().Throw<InvalidOperationException>()
                         .Which.Message.Should().Contain("the enumeration order of each assembly's loadable types")
                         .And.Contain("neither of which is specified")
                         .And.NotContain("the order in which an assembly defines its types");
        }

        [Fact]
        public void MustProbeTheAssemblySetCapturedAtRegistrationWhenTheSourceGrowsAfterwards()
        {
            var sourceAssemblies = new List<Assembly>
            {
                New.Common().Assembly.WithFullName("Chatter.Fake.Registered").WithTypes(typeof(FakeFirstCommandHandler)).Creation
            };
            var sourceProvider = new Mock<IAssemblyFilterSourceProvider>();
            sourceProvider.Setup(provider => provider.GetSourceAssemblies()).Returns(() => sourceAssemblies.ToList());
            var chatterBuilder = new ServiceCollection().AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                                        messageHandlerSourceBuilder: b => b.WithSourceProvider(sourceProvider.Object)
                                                                                                           .WithNamespaceSelector("Chatter.Fake*"));

            sourceAssemblies.Add(New.Common().Assembly.WithFullName("Chatter.Fake.Late").WithTypes(typeof(FakeSecondCommandHandler)).Creation);

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        /// <summary>
        /// Characterization pin, not a red-first test: a builder whose service collection carries no scan record, here
        /// one made by the public <see cref="ChatterBuilder.Create(IServiceCollection, IConfiguration, IAssemblySourceFilter)"/>
        /// over a fresh collection, is checked against the result of applying its filter.
        /// </summary>
        [Fact]
        public void MustProbeTheFilterWhenTheServiceCollectionCarriesNoScanRecord()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler)).Creation;
            var filter = AssemblySourceFilterBuilder.New().WithExplicitAssemblies(assembly).Build();
            var chatterBuilder = ChatterBuilder.Create(new ServiceCollection(), Mock.Of<IConfiguration>(), filter);

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().ThrowExactly<InvalidOperationException>()
                         .Which.Message.Should().Contain(typeof(FakeFirstCommandHandler).FullName)
                         .And.Contain(typeof(FakeSecondCommandHandler).FullName);
        }

        [Fact]
        public void MustReportCompetingHandlersAcrossTwoAddChatterCqrsCallsOnOneServiceCollection()
        {
            var services = new ServiceCollection();
            AddChatterCqrsScanningNamedAssembly(services, "Chatter.Fake.First", typeof(FakeFirstCommandHandler));
            var secondChatterBuilder = AddChatterCqrsScanningNamedAssembly(services, "Chatter.Fake.Second", typeof(FakeSecondCommandHandler));

            services.Should().ContainSingle(sd => sd.ServiceType == typeof(IMessageHandler<FakeCommand>))
                    .Which.ImplementationType.Should().Be(typeof(FakeSecondCommandHandler));
            FluentActions.Invoking(() => secondChatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().Throw<InvalidOperationException>()
                         .Which.Message.Should().Contain(typeof(FakeFirstCommandHandler).FullName)
                         .And.Contain(typeof(FakeSecondCommandHandler).FullName);
        }

        [Fact]
        public void MustReportCompetingHandlersWhenCheckedOnTheBuilderFromTheEarlierCall()
        {
            var services = new ServiceCollection();
            var firstChatterBuilder = AddChatterCqrsScanningNamedAssembly(services, "Chatter.Fake.First", typeof(FakeFirstCommandHandler));
            AddChatterCqrsScanningNamedAssembly(services, "Chatter.Fake.Second", typeof(FakeSecondCommandHandler));

            FluentActions.Invoking(() => firstChatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().Throw<InvalidOperationException>()
                         .Which.Message.Should().Contain(typeof(FakeFirstCommandHandler).FullName)
                         .And.Contain(typeof(FakeSecondCommandHandler).FullName);
        }

        /// <summary>
        /// Characterization pin, not a red-first test: an assembly two <c>AddChatterCqrs</c> calls on one collection
        /// both scan contributes its handler once, not as a competing pair. No mutation of Chatter code was observed to
        /// redden it: dropping both the scan record's dedupe and the probe's <c>Distinct()</c> leaves it green, because
        /// Scrutor's <c>FromAssemblies</c> collects the scanned types into a set. Locks that ground truth so a Scrutor
        /// upgrade breaks this test instead of reporting a false competing pair.
        /// </summary>
        [Fact]
        public void MustNotReportOneHandlerWhenTheSameAssemblyIsScannedByTwoCalls()
        {
            var assembly = New.Common().Assembly.WithFullName("Chatter.Fake.Shared").WithTypes(typeof(FakeFirstCommandHandler)).Creation;
            var services = new ServiceCollection();
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly));
            var chatterBuilder = services.AddChatterCqrs(Mock.Of<IConfiguration>(), messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustProbeTheScanRecordThroughABuilderThatOnlyForwardsItsServices()
        {
            var sourceAssemblies = new List<Assembly>
            {
                New.Common().Assembly.WithFullName("Chatter.Fake.Registered").WithTypes(typeof(FakeFirstCommandHandler)).Creation
            };
            var sourceProvider = new Mock<IAssemblyFilterSourceProvider>();
            sourceProvider.Setup(provider => provider.GetSourceAssemblies()).Returns(() => sourceAssemblies.ToList());
            var chatterBuilder = new ForwardingChatterBuilder(
                new ServiceCollection().AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                       messageHandlerSourceBuilder: b => b.WithSourceProvider(sourceProvider.Object)
                                                                                          .WithNamespaceSelector("Chatter.Fake*")));

            sourceAssemblies.Add(New.Common().Assembly.WithFullName("Chatter.Fake.Late").WithTypes(typeof(FakeSecondCommandHandler)).Creation);

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustNotReportAHandlerScannedOnlyByACollectionItsDescriptorsWereCopiedInto()
        {
            var firstServices = new ServiceCollection();
            var firstChatterBuilder = AddChatterCqrsScanningNamedAssembly(firstServices, "Chatter.Fake.First", typeof(FakeFirstCommandHandler));
            var secondServices = CopyDescriptorsOf(firstServices);

            AddChatterCqrsScanningNamedAssembly(secondServices, "Chatter.Fake.Second", typeof(FakeSecondCommandHandler));

            FluentActions.Invoking(() => firstChatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        /// <summary>
        /// Characterization pin, not a red-first test: a scan record copied into another collection with its
        /// descriptors is that collection's record, so a later <c>AddChatterCqrs</c> call on it extends the copy and the
        /// check on its builder reports a handler the copied scan registered and the later call displaced.
        /// Mutation observed to redden it: keying the record on the collection that wrote it, so that the write and
        /// <see cref="HandlerScanRecord.FindScannedAssemblies(IServiceCollection)"/> ignore a record another collection wrote.
        /// </summary>
        [Fact]
        public void MustReportCompetingHandlersBetweenACopiedScanAndALaterCall()
        {
            var firstServices = new ServiceCollection();
            AddChatterCqrsScanningNamedAssembly(firstServices, "Chatter.Fake.First", typeof(FakeFirstCommandHandler));
            var secondServices = CopyDescriptorsOf(firstServices);

            var secondChatterBuilder = AddChatterCqrsScanningNamedAssembly(secondServices, "Chatter.Fake.Second", typeof(FakeSecondCommandHandler));

            FluentActions.Invoking(() => secondChatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().Throw<InvalidOperationException>()
                         .Which.Message.Should().Contain(typeof(FakeFirstCommandHandler).FullName)
                         .And.Contain(typeof(FakeSecondCommandHandler).FullName);
        }

        /// <summary>
        /// The second collection runs its own <c>AddChatterCqrs</c> BEFORE the first collection's descriptors are copied
        /// in, and nothing is added afterwards, so it carries two record descriptors: its own first, the copied one
        /// after it. Copying first and adding afterwards would fold the copy into one record on the write and pass
        /// without the read ever seeing two records.
        /// </summary>
        [Fact]
        public void MustProbeEveryScanRecordTheCollectionCarries()
        {
            IServiceCollection secondServices = new ServiceCollection();
            var secondChatterBuilder = AddChatterCqrsScanningNamedAssembly(secondServices, "Chatter.Fake.Second", typeof(FakeSecondCommandHandler));
            var firstServices = new ServiceCollection();
            AddChatterCqrsScanningNamedAssembly(firstServices, "Chatter.Fake.First", typeof(FakeFirstCommandHandler));

            foreach (var descriptor in firstServices)
            {
                secondServices.Add(descriptor);
            }

            FluentActions.Invoking(() => secondChatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().Throw<InvalidOperationException>()
                         .Which.Message.Should().Contain(typeof(FakeFirstCommandHandler).FullName)
                         .And.Contain(typeof(FakeSecondCommandHandler).FullName);
        }

        /// <summary>
        /// Characterization pin, not a red-first test: the check on a collection that carries two record descriptors
        /// leaves both in place. Mutation observed to redden it: collapsing the records into one while reading them,
        /// only when there are several, which <see cref="MustLeaveTheApplicationServiceCollectionUntouched"/> does not
        /// see because it carries one record.
        /// </summary>
        [Fact]
        public void MustLeaveACollectionCarryingSeveralScanRecordsUntouched()
        {
            IServiceCollection secondServices = new ServiceCollection();
            var secondChatterBuilder = AddChatterCqrsScanningNamedAssembly(secondServices, "Chatter.Fake.Second", typeof(FakeSecondCommandHandler));
            var firstServices = new ServiceCollection();
            AddChatterCqrsScanningNamedAssembly(firstServices, "Chatter.Fake.First", typeof(FakeFirstCommandHandler));

            foreach (var descriptor in firstServices)
            {
                secondServices.Add(descriptor);
            }

            var descriptorsBeforeCheck = secondServices.ToList();

            FluentActions.Invoking(() => secondChatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().Throw<InvalidOperationException>();
            secondServices.Should().Equal(descriptorsBeforeCheck);
        }

        private static IServiceCollection CopyDescriptorsOf(IServiceCollection services)
        {
            IServiceCollection copiedServices = new ServiceCollection();

            foreach (var descriptor in services)
            {
                copiedServices.Add(descriptor);
            }

            return copiedServices;
        }

        private IChatterBuilder AddChatterCqrsScanningNamedAssembly(IServiceCollection services, string assemblyFullName, Type handlerType)
        {
            var assembly = New.Common().Assembly.WithFullName(assemblyFullName).WithTypes(handlerType).Creation;
            return services.AddChatterCqrs(Mock.Of<IConfiguration>(),
                                           messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly));
        }

        private static int PositionOf(string message, Type type)
            => message.IndexOf(type.FullName, StringComparison.Ordinal);

        private IChatterBuilder AddChatterCqrsScanning(params Type[] scannedTypes)
        {
            var assembly = New.Common().Assembly.WithTypes(scannedTypes).Creation;
            return new ServiceCollection().AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                          messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly));
        }

        /// <summary>
        /// A transparent <see cref="IChatterBuilder"/> wrapper that exposes only what the public contract obliges.
        /// </summary>
        private class ForwardingChatterBuilder : IChatterBuilder
        {
            private readonly IChatterBuilder _innerChatterBuilder;

            public ForwardingChatterBuilder(IChatterBuilder innerChatterBuilder) => _innerChatterBuilder = innerChatterBuilder;

            public IServiceCollection Services => _innerChatterBuilder.Services;
            public IConfiguration Configuration => _innerChatterBuilder.Configuration;
            public IAssemblySourceFilter AssemblySourceFilter => _innerChatterBuilder.AssemblySourceFilter;
        }

        private class FakeCommand : ICommand { }
        private class FakeFirstCommandHandler : IMessageHandler<FakeCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeSecondCommandHandler : IMessageHandler<FakeCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        [CompilerGenerated]
        private class FakeCompilerGeneratedCommandHandler : IMessageHandler<FakeCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeGenericCommand<TPayload> : ICommand { }
        private class FakeFirstGenericCommandHandler : IMessageHandler<FakeGenericCommand<string>>
        {
            public Task Handle(FakeGenericCommand<string> message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeSecondGenericCommandHandler : IMessageHandler<FakeGenericCommand<string>>
        {
            public Task Handle(FakeGenericCommand<string> message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeSinglyHandledCommand : ICommand { }
        private abstract class FakeAbstractCommandHandler : IMessageHandler<FakeSinglyHandledCommand>
        {
            public abstract Task Handle(FakeSinglyHandledCommand message, IMessageHandlerContext context);
        }

        private class FakeConcreteCommandHandler : FakeAbstractCommandHandler
        {
            public override Task Handle(FakeSinglyHandledCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeOpenGenericCommandHandler<TUnused> : IMessageHandler<FakeSinglyHandledCommand>
        {
            public Task Handle(FakeSinglyHandledCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeEvent : IEvent { }
        private class FakeEventHandler : IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeEventHandler2 : IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeDualRoleCommand : ICommand { }
        private class FakeDualRoleHandler : IMessageHandler<FakeDualRoleCommand>, IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeDualRoleCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }
    }
}
