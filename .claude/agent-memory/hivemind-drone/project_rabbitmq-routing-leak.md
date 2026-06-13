---
name: rabbitmq-routing-leak
description: Why RabbitMqReceiver must NOT stamp inbound delivery exchange/routing-key onto context (outbound override leak)
metadata:
  type: project
---

RabbitMqMessageContext.TargetExchange / RoutingKey are OUTBOUND publish-override command keys — only `WithRabbitMqRouting(exchange,key)` writes them and only RabbitMqSender.ResolveAddress reads them. The receiver must NEVER stamp the broker-supplied inbound delivery.Exchange / delivery.RoutingKey into those same context keys.

**Why:** Core BrokeredMessageDispatcher seeds an outbound send's options from the inbound MessageContext (SendOptions.Create(inbound).Merge → RoutingOptions.Merge copies the full inbound dict into outbound). If the receiver stamps the inbound delivery address into TargetExchange/RoutingKey, every receive-then-send/publish follow-up gets silently re-routed back toward the inbound queue (default-exchange receive => follow-ups re-routed to the receive queue). Confirmed HIGH review finding, fixed 2026-06-13.

**How to apply:** Keep inbound exchange/routing-key OFF the context entirely — grep confirmed the sender is the ONLY production reader and nothing downstream consumes the inbound address, so observation keys were intentionally NOT added (simplest leak closure). If a future need to observe the inbound address arises, add DISTINCT read-only keys (e.g. ReceivedExchange/ReceivedRoutingKey) the sender never reads — do not reuse the command keys. See [[rabbitmq-receiver-core]].
