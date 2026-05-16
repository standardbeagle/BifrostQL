---
title: Workflows
description: Define and run metadata-driven workflows through the BifrostQL mutation pipeline.
---

BifrostQL workflows are declarative operation sequences stored in the app-metadata overlay. They let an application describe a named business operation without hand-writing a new data access path.

Every workflow step runs through the same BifrostQL executor used by direct GraphQL requests, so tenant filters, authorization policy, state-machine validation, and audit behavior still apply.

## Definition Shape

Add workflows to the root `workflows` array in app metadata:

```json
{
  "workflows": [
    {
      "name": "record-payment",
      "trigger": { "type": "manual" },
      "inputs": {
        "type": "object",
        "required": ["invoiceId", "amountCents"]
      },
      "steps": [
        {
          "name": "invoice",
          "type": "query",
          "payload": {
            "table": "dues_invoices",
            "id": "{{ inputs.invoiceId }}",
            "required": true
          },
          "output": "invoice"
        },
        {
          "name": "payment",
          "type": "mutation",
          "payload": {
            "table": "dues_payments",
            "action": "insert",
            "values": {
              "tenant_id": "{{ steps.invoice.tenant_id }}",
              "invoice_id": "{{ inputs.invoiceId }}",
              "amount_cents": "{{ inputs.amountCents }}"
            }
          }
        }
      ]
    }
  ]
}
```

Templates in the form `{{ inputs.name }}` and `{{ steps.output.column }}` are resolved before each step runs.

## Step Types

- `query`: reads one row by primary key through `IWorkflowDataExecutor.QuerySingleAsync`.
- `mutation`: inserts or updates a row through the generated GraphQL mutation pipeline.
- `transition`: reads the current row and updates the configured state column, letting the state-machine validator decide whether the transition is allowed.
- `policy-check`: validates table/action intent before later side effects.
- `audit`: inserts an `audit_log` row.
- `branch`: runs inline steps when a simple condition matches.
- `parallel`: runs inline steps concurrently.

## Triggers

Workflows support four trigger types:

- `manual`: called explicitly by application code or an HTTP shim.
- `on-mutation`: runs after a matching generated mutation succeeds.
- `on-state-transition`: runs after a matching state-machine transition succeeds.
- `scheduled`: runs from the in-process scheduler tick.

Non-manual triggers set an internal suppression flag while running so workflow-generated mutations do not recursively trigger more workflows.

The built-in scheduler is in-process and single-instance. It is suitable for local automation and sample applications; durable distributed scheduling is intentionally left to an external job runner or a follow-up integration.
