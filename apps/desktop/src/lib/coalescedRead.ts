/** One read per group in a burst; events arriving in flight get one follow-up. */
export function createCoalescedRead<K extends string>(read: (groups: ReadonlySet<K>) => Promise<void>, delay = 40) {
  const pending = new Set<K>()
  let waiters: Array<{ resolve(): void; reject(error: unknown): void }> = []
  let timer: ReturnType<typeof setTimeout> | null = null
  let running = false
  let flight = 0
  function schedule() {
    if (running || timer || !pending.size) return
    timer = setTimeout(() => { timer = null; void drain() }, delay)
  }
  async function drain() {
    if (running || !pending.size) return
    running = true
    const owner = ++flight
    const groups = new Set(pending), batch = waiters
    pending.clear(); waiters = []
    try { await read(groups); for (const waiter of batch) waiter.resolve() }
    catch (error) { for (const waiter of batch) waiter.reject(error) }
    finally { if (owner === flight) { running = false; schedule() } }
  }
  return {
    request(...groups: K[]) {
      for (const group of groups) pending.add(group)
      const promise = new Promise<void>((resolve, reject) => waiters.push({ resolve, reject }))
      schedule()
      return promise
    },
    reset() {
      flight++; running = false
      if (timer) clearTimeout(timer)
      timer = null; pending.clear()
      for (const waiter of waiters) waiter.resolve()
      waiters = []
    },
  }
}
