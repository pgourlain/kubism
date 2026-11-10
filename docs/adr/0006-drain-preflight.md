# 0006. Drain pre-flight

Status: accepted

## Context
Moving the pods of a node pool to another one means cordoning and draining every node of the old pool. `kubectl drain` only finds out what cannot move while it runs: a bare pod is deleted for good, a pod pinned to the old pool by a `nodeSelector` comes back Pending, a PodDisruptionBudget with no room blocks until the timeout. Kubism's `DrainAsync` behaved the same (and cordoned before failing).

## Decision
- `Core/Drain/DrainPreflight.Analyze` predicts, from objects read once (`DrainPreflightLoader`: nodes, pods, ReplicaSets, Jobs, PDBs, PVCs, PVs), one verdict per pod on the selected nodes: *Ok*, *Warning* or *Blocker*, with the reason and the node it would move to. It is pure and unit-tested.
- Placement is greedy over the nodes that remain (ready, not cordoned, not selected), largest requests first, consuming their room as pods are placed. Predicates (`Scheduling`): nodeSelector, required node affinity (In, NotIn, Exists, DoesNotExist, Gt, Lt, matchFields), NoSchedule/NoExecute taints against tolerations, PersistentVolume node affinity (zonal disks, local volumes), host ports, required pod affinity and anti-affinity, `DoNotSchedule` topology spread, CPU, memory and pod-count room.
- Blockers: bare pod; no remaining node fits; DaemonSet pod when DaemonSets are not ignored; emptyDir without "delete emptyDir data"; a PDB with `disruptionsAllowed = 0` while every replica is healthy (the eviction can never be allowed). A PDB that allows fewer disruptions than pods to move, or is unhealthy right now, is a warning: evictions are serialized or wait.
- `ClusterActions.DrainMany` runs the same analysis itself and refuses (`DrainBlockedException`, nothing changed) unless `DrainOptions.Force`, so the UI and the MCP server share the guard. It cordons **all** nodes before evicting any.
- What could not be read (for example RBAC forbids listing PDBs) is a note in the report, never a silent "ok".

- The UI is a dedicated page, not a dialog on the node list, so that nothing that writes sits next to a list: choosing nodes and reading the plan are read-only; one clearly separated button drains, after a confirmation. Node pools are found through the usual pool labels (`Scheduling.NodeGroupLabels`: AKS `agentpool`, EKS node group, GKE node pool, Karpenter…), and the label can be chosen by hand, so nothing is specific to one provider.

## Limits
An approximation of kube-scheduler: no preemption or PriorityClass, no cluster autoscaler scale-up, no soft (preferred) affinities, no reverse anti-affinity (existing pods' terms against the incoming pod), no `minDomains`, no admission webhooks or Azure Policy. The report says "predicted". The server-side re-check and the eviction retries stay the safety net.
