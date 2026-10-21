import type { DeploymentHistory, DeploymentSnapshot } from "./api"

export function snapshot(): DeploymentSnapshot {
  return {
    observedAt: new Date().toISOString(),
    staleAfterSeconds: 20,
    namespace: "kardia-ha-lab",
    workloads: ["api", "frontend", "controller", "worker"].map((role) => ({
      name: `kardia-${role}`,
      kind:
        role === "worker"
          ? "DaemonSet"
          : role === "controller"
            ? "StatefulSet"
            : "Deployment",
      strategy:
        role === "worker" || role === "controller"
          ? "OnDelete"
          : "RollingUpdate",
      generation: 2,
      observedGeneration: 2,
      desired: 1,
      updated: role === "worker" ? 0 : 1,
      ready: 1,
      available: 1,
      matchingImages: role === "worker" ? 0 : 1,
      state: role === "worker" ? "AwaitingReplacement" : "InSync",
      targetImages: { app: "kardia:ha-lab-new" },
      conditions: [],
    })),
    pods: [
      {
        name: "kardia-worker-old",
        uid: "old-pod",
        role: "kardia-worker",
        nodeName: "kardia-lab-01",
        phase: "Running",
        ready: false,
        terminating: false,
        createdAt: new Date().toISOString(),
        conditions: [
          { type: "Ready", status: "False", reason: "ContainersNotReady" },
        ],
        containers: [
          {
            name: "worker",
            init: false,
            image: "kardia:ha-lab-old",
            imageId: "sha256:old-image",
            state: "Waiting",
            reason: "CrashLoopBackOff",
            exitCode: null,
            restarts: 4,
            ready: false,
          },
        ],
      },
    ],
    jobs: [
      {
        name: "kardia-migrate-new",
        createdAt: new Date().toISOString(),
        finishedAt: new Date().toISOString(),
        state: "Succeeded",
        active: 0,
        succeeded: 1,
        failed: 0,
        images: ["kardia-migrate:ha-lab-new"],
        conditions: [{ type: "Complete", status: "True", reason: null }],
      },
    ],
  }
}

export function history(): DeploymentHistory {
  return {
    observedAt: new Date().toISOString(),
    unconfirmedAfterSeconds: 600,
    operations: [
      {
        id: "a0000000-0000-0000-0000-000000000001",
        mode: "Stage",
        target: "ha-lab-new",
        actor: "b0000000-0000-0000-0000-000000000002",
        actorKind: "User",
        startedAt: new Date().toISOString(),
        lastReportedAt: new Date().toISOString(),
        status: "Succeeded",
        sequence: 2,
        events: [
          {
            sequence: 1,
            at: new Date().toISOString(),
            phase: "Build",
            subject: null,
            outcome: "Running",
            description: "Build <script>alert(1)</script>",
          },
          {
            sequence: 2,
            at: new Date().toISOString(),
            phase: "Complete",
            subject: null,
            outcome: "Succeeded",
            description: "Staging finished; OnDelete pods unchanged",
          },
        ],
      },
    ],
  }
}
