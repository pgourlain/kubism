# ADR 0003: Azure Monitor managed Prometheus authentication

Status: accepted. Date: 2026-10.

## Context
Right-sizing history on AKS comes from Azure Monitor managed Prometheus, queried with an Entra token for scope `https://prometheus.monitor.azure.com/.default`. Two ways to get the token: the Azure CLI login, or reuse what `kubelogin` already obtained.

## Decision
Use the **Azure CLI credential**: `az account get-access-token --resource https://prometheus.monitor.azure.com`, cached until two minutes before expiry (`AzureCliTokenProvider`, behind `ITokenProvider`).

## Why
* `kubelogin` stores its tokens in a private cache whose format and audience are for the AKS server application, not for Prometheus; reusing them would couple Kubism to undocumented internals and the wrong audience.
* `az` is already a prerequisite of the typical AKS workflow and supports device-code, SSO and managed identity.
* `ITokenProvider` keeps the door open for a built-in MSAL flow later.

## Consequences
The Azure CLI must be on `PATH` (the login-shell `PATH` is resolved at startup for GUI launches). When it is missing the error says so and suggests `az login`.
