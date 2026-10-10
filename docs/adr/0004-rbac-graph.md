# ADR 0004: RBAC graph, semantics and limits

Status: accepted. Date: 2026-10.

## Context

Kubism explains Kubernetes rights as graphs: subject → binding → role → rules, "what can X do" and "who can do Y". The answers must match
what the API server's RBAC authorizer says (the one behind `kubectl auth can-i`), or the page misleads.

## Decision

`RbacGraph` (`Kubism.Core/Security`) is a pure, immutable model built from the four RBAC kinds. `RbacStore` keeps one current through the
session's `ResourceCache` (four shared informers, a new graph after each batch of changes). It is read-only: nothing goes through `ClusterActions`.

Semantics implemented, as the authorizer defines them:

* **Matching.** `*` in verbs, apiGroups and resources; an empty apiGroup is the core group. A resource entry matches `*`, the exact
  `resource[/subresource]`, `*/sub` (that subresource of anything) and `resource/*` (any subresource of that resource). A plain `pods` does
  **not** cover `pods/exec`. `resourceNames` restrict a rule to named objects, so a request without a name (list, watch, create) never matches it.
  `nonResourceURLs` match exactly, with `*`, or by a trailing `*` prefix.
* **Scope.** A RoleBinding grants inside its own namespace only, including when it points at a ClusterRole (the chain view marks that edge:
  "ClusterRole applied in <ns> only"; cluster-scoped resources and non-resource URLs are not granted through it). A ClusterRoleBinding grants
  everywhere. A request with no namespace is cluster-level: only ClusterRoleBindings count (what `kubectl auth can-i --all-namespaces` sees).
* **Aggregation.** A ClusterRole with `aggregationRule` gets the rules of every ClusterRole whose labels match one of its selectors
  (`matchLabels` and `matchExpressions`), recursively and cycle-safe, added to its own rules. Real clusters already hold the aggregated rules
  (the controller writes them); the union makes the same result when it has not run yet.
* **Identities.** A user is in `system:authenticated` (`system:anonymous` is in `system:unauthenticated`). A service account is the user
  `system:serviceaccount:<ns>:<name>` in `system:serviceaccounts`, `system:serviceaccounts:<ns>` and `system:authenticated`. A User subject
  with that name matches the service account too. The group `system:masters` is authorized outside RBAC and is reported as such.
  Real groups (from Entra, OIDC…) are given by the caller, from `SelfSubjectReview`.
* **Orphans.** A binding to a role that does not exist grants nothing and is reported; a ClusterRoleBinding cannot reference a Role.
* **Additive.** There is no deny.
* **Risk flags** use the same matcher, so a wildcard counts: `*` verbs or resources, `escalate`, `bind`, `impersonate`, `create pods/exec`,
  `create pods/attach`, `get/list/watch secrets`, `create pods`, `cluster-admin`, and bindings to `system:authenticated`,
  `system:serviceaccounts`, `system:unauthenticated` or `system:anonymous`. A rule limited by `resourceNames` is flagged at a lower level.

**Cross-check with the server.** `RbacVerifier` asks `SelfSubjectReview` for the real identity, then `SelfSubjectAccessReview` for the
current user and compares. A gap is shown, never hidden. `SubjectAccessReview` for another subject is only offered when `SelfSubjectAccessReview`
says the user may `create subjectaccessreviews`. These are POST requests that create nothing; `RbacVerifier.cs` is allowed next to
`AccessReview.cs` in the architecture test that bans mutating verbs outside `ClusterActions`.

**Incomplete data is stated.** When Roles, ClusterRoles or either binding kind cannot be listed (typically a 403), `RbacGraph.Availability`
says which, and every answer (graph, subject view, who-can, MCP) carries the warning instead of a silently partial picture.

## Limits (known)

* **Other authorizers are invisible.** Node, webhook authorizers, **Azure RBAC for Kubernetes** (AKS) and ABAC answer outside the RBAC objects.
  The graph can say "denied" where the server says "allowed" and the reverse; the "Ask the API server" buttons show the difference.
* **Group membership is unknown** beyond the implicit groups, unless the caller supplies it. On AKS with Entra ID, Group subjects are Entra object
  IDs and are shown as such; resolving them to names through Microsoft Graph is out of scope (future task).
* **Resource scope is not looked up.** Whether a resource is namespaced comes from the request (a namespace given or not), not from discovery.
  A request without an API group uses the group of well-known resources, else any group matches (stated in the answer).
* **Tests.** The expected answers in `RbacGraphTests` are derived by hand from the authorizer's documented rules on `Data/rbac.yaml`; they have
  **not yet been captured from `kubectl auth can-i` on a kind cluster** (open item in tasks.md).
* The graph is built from all namespaces; a user who can only list RBAC objects in some namespaces gets the incomplete-data warning.
