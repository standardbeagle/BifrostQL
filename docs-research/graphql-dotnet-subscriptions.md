# GraphQL .NET Subscriptions

Subscriptions in GraphQL .NET provide real-time data updates through the use of `IObservable<T>`. This document outlines how to implement and use subscriptions in your GraphQL .NET application.

## Overview

Subscriptions differ from queries and mutations in that they maintain a persistent connection to receive real-time updates. They use the `subscription` keyword instead of `query` or `mutation`.

## Server Requirements

To use subscriptions, you need:
1. A server that supports the Subscription protocol
2. The [GraphQL Server](https://github.com/graphql-dotnet/server/) project which implements the Apollo GraphQL subscription protocol

## Implementation Example

Here's how to implement a subscription:

```graphql
subscription MessageAdded {
  messageAdded {
    from {
      id
      displayName
    }
    content
    sentAt
  }
}
```

And the corresponding C# implementation:

```csharp
public class ChatSubscriptions : ObjectGraphType
{
  private readonly IChat _chat;

  public ChatSubscriptions(IChat chat)
  {
    _chat = chat;

    Field<MessageType, Message>("messageAdded")
      .ResolveStream(ResolveStream);
  }

  private IObservable<Message> ResolveStream(IResolveFieldContext context)
  {
    return _chat.Messages();
  }
}
```

## Key Points

1. Use the `subscription` keyword in your GraphQL operations
2. Implement `ObjectGraphType` for your subscription class
3. Use `ResolveStream` to handle the subscription resolution
4. Return an `IObservable<T>` from your resolver

## References

- [Full Schema Example](https://github.com/graphql-dotnet/graphql-dotnet/blob/master/src/GraphQL.Tests/Subscription/SubscriptionSchema.cs)
- [GraphQL Server Project Samples](https://github.com/graphql-dotnet/server/tree/develop/samples)