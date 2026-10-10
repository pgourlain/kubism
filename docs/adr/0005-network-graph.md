# ADR 0005: Network graph, how exposure is derived and what is not visible

Status: accepted. Date: 2026-10.

## Context
Kubism shows how traffic reaches the pods: exposure → Ingress → Service → workload, with ports on every hop. "Public" means reachable from outside the cluster, which the cluster's own objects can say; guessing from IP ranges or annotations cannot be right.

## Decision
`NetworkGraph` (`Kubism.Core/Network`) is a pure, immutable model built from Ingresses, IngressClasses, Services, EndpointSlices and Pods. `NetworkStore` keeps it current through the session's `ResourceCache` (five shared informers); `NetworkLoader` builds one for the MCP server. Read-only: nothing goes through `ClusterActions`.

**Exposure levels** (`ExposureLevel`):
* **Outside the cluster:** a Service of type `LoadBalancer` or `NodePort`, or with `externalIPs`; an Ingress whose controller's Service is such a Service; an ingress controller that runs outside the cluster (Azure Application Gateway / AGIC, AWS ALB, GCE); pods with `hostPort` or `hostNetwork`. An internal cloud load balancer (annotations such as `azure-load-balancer-internal`) is outside the cluster too: it only adds the note "private network".
* **In-cluster only:** everything else. An Ingress whose controller Service is `ClusterIP` is in-cluster, and the graph says so.
* **Unknown:** the Ingress has no class, or its controller's Service cannot be found. Flagged as a problem; never presented as in-cluster or outside.
The level flows from the exposure nodes along the edges (the highest wins), so a Service reached by an exposed and an unexposed Ingress is outside overall while each Ingress keeps its own level.

**Finding the controller.** Through `IngressClass.spec.controller` (or the `kubernetes.io/ingress.class` annotation, or the default class), mapped to the controller's Service by the labels each controller's chart sets (`ingress-nginx`, `nginx-ingress`, `traefik`, `haproxy-ingress`, `contour`/`envoy`, Istio `ingressgateway`). When several controllers of one kind exist (public and internal), the one whose Helm instance is named after the class wins, else the one whose name contains it. Admission and metrics Services are never taken for the controller. The controller's Service is drawn as the exposure node, not as a Service chain.

**Ports.** Ingress backend port (number or name) → Service port → `targetPort` (a name is resolved against the selected pods' container ports) → container port. Workloads come from the pods' owner chain; a Deployment is recognised from its ReplicaSet's `pod-template-hash`, a CronJob from its Job's name, without reading ReplicaSets or Jobs.

**Problems** are put where the fault is: a missing backend Service or port on the Ingress and the hop (not on a Service that serves other Ingresses fine); an unresolved named `targetPort` on the Service and the hop; no ready endpoint / selector matching no pod on the Service; pods not ready on the workload; missing or expiring TLS secret on the Ingress; no ingress class or no controller found on the Ingress; a Service exposed directly (not through an Ingress) and host ports.

**TLS secrets** are looked up one by one (`GET`, cached 5 minutes): only existence and the public certificate's expiry are read, and nothing of the secret is kept (rule 4). A secret that cannot be read (403) is "not checked", never "missing".

**Namespace filter.** A chain is kept when its Ingress, Service or workload is in a chosen namespace. Exposure nodes of another namespace (the controller's Service) stay as dimmed context, so the path to the outside is never cut, but the other chains they feed are not pulled in. Services nothing routes to are hidden unless asked for, except when looking for problems.

**Incomplete data is stated:** a kind that cannot be listed (typically 403) is named in a banner and in the MCP answer instead of a silently partial graph.

## Not visible (known limits)
* DNS, firewalls, network security groups, WAF, Front Door, service mesh policy and cloud load balancer rules.
* **NetworkPolicy** (what is blocked even though a path exists) and **Gateway API** (`Gateway`, `HTTPRoute`) are not read yet: a second batch.
* A controller whose Service does not carry the usual labels is reported as "controller not found" (exposure unknown); there is no per-class override yet.
* Ingress backends that are `resource` references (not Services) are ignored.
* Tests: the fixture (`Data/network.yaml`) is hand-built from the usual shapes of ingress-nginx, AGIC and Traefik; it has not been compared with real AKS clusters yet (manual validation in tasks.md).
