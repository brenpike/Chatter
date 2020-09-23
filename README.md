# Chatter

Chatter enables rapid development of domain driven .NET Core Web APIs and Microservices. The core libraries of Chatter are:
* [Chatter.CQRS](./src/Chatter.CQRS/src/README.md#chatter-cqrs), and
* [Chatter.MessageBrokers](./src/Chatter.MessageBrokers/src/README.md#chatter-messagebrokers).  


##### <a name="intro-cqrs"></a> Chatter.Cqrs 

[Chatter.CQRS](./src/Chatter.CQRS/src/README.md#chatter-cqrs) enables the implementation of a CQRS architecture through the use of the mediator pattern. 
Commands are used to change the state of an aggregate, while Queries are used to retrieve data or "read models". 
In addition, the CQRS library allows the dispatching and handling of Events. Events originate as "Domain Events" 
from an API's internal aggregate(s) and once dispatched, may be handled within the originating domain or published as
"Integration Events" so that other APIs or Microservices can subscribe and take action. Dispatching and subscribing to "Integration Events"
requires the use of message broker infrastructure which is accomplished through leveraging [Chatter.MessageBrokers](#####intro-messagebrokers).

The CQRS library also exposes "Command Pipeline" functionality which allows implementation of cross-cutting concerns
applied across all command handlers, such as logging.


##### <a name="intro-messagebrokers"></a> Chatter.MessageBrokers

[Chatter.MessageBrokers](./src/Chatter.MessageBrokers/src/README.md#chatter-messagebrokers) exposes technology agnostic message broker functionality.
At its core, it enables the dispatching and subscribing of messages to message broker infrastructure, however, it also exposes advanced messaging functionality,
such as sagas, inbox, outbox, routing slips, etc. It is built on top of [Chatter.CQRS](#####intro-cqrs), leveraging the message dispatching and Command and Event handling
capabilities that it exposes. Brokered messages recieved from the infrastructure are relayed (dispatched) to Command or Event handlers, depending on the type of messages
received, giving a unified message handling experience for messages that originate internally or from external systems.

As mentioned, Chatter.MessageBrokers is technology/infrastructure agnostic, so it exposes interfaces that must be implemented for the message broker
infrastructure of choice. [Chatter.MessageBrokers.AzureServiceBus](./src/Chatter.MessageBrokers.AzureServiceBus/src/README.md#chatter-azureservicebus)
is one such implementation for Azure Service Bus.

The advanced features mentioned earlier often require other types of infrastructure, such as persistance. Interfaces exist which when implemented using
the technology/infrastructure of choice and will enable seamless integration with Chatter.

A step-by-step guide for creating a 'from scratch' scenario can be found [here](./samples/README.md). The scenario creates two brand new .NET Core Web APIs from scratch
and Chatter is used to facilitate communication between them.


# Table of Contents

##### Library Docs

* [Chatter.CQRS](./src/Chatter.CQRS/src/README.md#chatter-cqrs)
* [Chatter.MessageBrokers](./src/Chatter.MessageBrokers/src/README.md#chatter-messagebrokers)
* [Chatter.MessageBrokers.AzureServiceBus](./src/Chatter.MessageBrokers.AzureServiceBus/src/README.md#chatter-azureservicebus)

##### Getting Started

* [Building a 'from scratch' scenario](./samples/README.md)



