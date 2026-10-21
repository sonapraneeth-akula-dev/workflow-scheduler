import type { SystemObservation, SystemSnapshot } from "./api"

type FixtureObservation = Omit<SystemObservation, "snapshot"> & {
  snapshot: Omit<SystemSnapshot, "instances" | "workers"> & {
    instances: [
      SystemSnapshot["instances"][number],
      SystemSnapshot["instances"][number],
    ]
    workers: [SystemSnapshot["workers"][number]]
  }
}

export const observation: FixtureObservation = {
  respondingInstanceId: "11111111111111111111111111111111",
  snapshot: {
    observedAt: "2026-10-21T08:00:00Z",
    heartbeatTimeoutSeconds: 15,
    instances: [
      {
        id: "11111111111111111111111111111111",
        name: "kardia-api <script>",
        hostName: "api-host",
        nodeName: "kardia-lab-1",
        podName: "kardia-api-0",
        namespace: "kardia",
        role: "Api",
        version: "1.0.0",
        status: "Healthy",
        isLeader: false,
        startedAt: "2026-10-21T07:00:00Z",
        lastHeartbeatAt: "2026-10-21T07:59:59Z",
        httpRequests: 100,
        liveConnections: 2,
        liveSubscriptions: 4,
        liveDeliveries: 20,
      },
      {
        id: "22222222222222222222222222222222",
        name: "kardia-controller-0",
        hostName: "controller-host",
        nodeName: "kardia-lab-2",
        podName: "kardia-controller-0",
        namespace: "kardia",
        role: "ApiController",
        version: "1.0.0",
        status: "Healthy",
        isLeader: true,
        startedAt: "2026-10-21T07:00:00Z",
        lastHeartbeatAt: "2026-10-21T07:59:59Z",
        httpRequests: 50,
        liveConnections: 0,
        liveSubscriptions: 0,
        liveDeliveries: 0,
      },
    ],
    workers: [
      {
        id: "12345678-1234-4234-9234-123456789abc",
        name: "kardia-worker-0",
        pool: "lab",
        status: "Healthy",
        lastHeartbeatAt: "2026-10-21T07:59:59Z",
        maxTasks: 4,
        slotsUsed: 3,
        assignedTasks: 3,
        runningTasks: 2,
        activeDagNodes: 2,
        activeRuns: 1,
        activeWorkflows: 1,
      },
    ],
    totals: {
      queuedRuns: 1,
      runningRuns: 1,
      abortingRuns: 0,
      activeWorkflows: 2,
      assignedTasks: 3,
      runningTasks: 2,
      activeDagNodes: 2,
    },
  },
}
