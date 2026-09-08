import { createQuantityValues, type EstimateInput, type ProcessInput, type SubassemblyEstimateInput, type SubassemblyInput } from './types.ts'

/** Roll direct BOM edge usage into each imported child's total demand per top-level part. */
export function reconcileSubassemblyQuantities<T extends EstimateInput>(input: T): T {
  if (input.kind !== 'subassembly') return input
  const children = new Map(input.subassemblies.map((child) => [child.id, child]))
  const totals = new Map<string, number>()
  const visit = (processes: ProcessInput[], usage: number, ancestors: Set<string>) => {
    processes.forEach((process) => {
      const id = process.subassemblyId
      const child = id ? children.get(id) : undefined
      if (!id || !child || ancestors.has(id)) return // The calculator reports invalid links/cycles.
      const extended = usage * (process.quantityPerParent ?? 1)
      totals.set(id, (totals.get(id) ?? 0) + extended)
      visit(child.processes, extended, new Set(ancestors).add(id))
    })
  }
  visit(input.processes, 1, new Set())
  return {
    ...input,
    subassemblies: input.subassemblies.map((child) => {
      const total = totals.get(child.id)
      return child.deriveQuantitiesFromParent && total !== undefined ? {
        ...child,
        quantityPerParent: total,
        quantitiesByParentQuantity: createQuantityValues((quantity) => quantity * total, input.quantities),
      } : child
    }),
  }
}

export function updateSubassemblyGraph(input: SubassemblyEstimateInput, id: string,
  update: (current: SubassemblyInput) => SubassemblyInput): SubassemblyEstimateInput {
  const previous = input.subassemblies.find((child) => child.id === id)
  if (!previous) return input
  const updated = update(previous)
  const usage = Math.max(0.000001, updated.quantityPerParent ?? 1)
  const usageChanged = usage !== (previous.quantityPerParent ?? 1)
  const nameChanged = updated.partNumber !== previous.partNumber
  const next = usageChanged ? {
    ...updated, quantityPerParent: usage,
    quantitiesByParentQuantity: createQuantityValues((quantity) => quantity * usage, input.quantities),
  } : updated
  // Scale all incoming edges proportionally, including links inside another subassembly.
  const ratio = usage / (previous.quantityPerParent ?? 1)
  const updateLinks = (processes: ProcessInput[]) => processes.map((process) => process.subassemblyId === id ? {
    ...process,
    ...(nameChanged ? { description: next.partNumber.trim() || process.description } : {}),
    ...(usageChanged ? { quantityPerParent: (process.quantityPerParent ?? 1) * ratio } : {}),
  } : process)
  return reconcileSubassemblyQuantities({
    ...input,
    processes: updateLinks(input.processes),
    subassemblies: input.subassemblies.map((child) => {
      const value = child.id === id ? next : child
      return { ...value, processes: updateLinks(value.processes) }
    }),
  })
}

export function removeSubassemblyGraph(input: SubassemblyEstimateInput, id: string): SubassemblyEstimateInput {
  const children = new Map(input.subassemblies.map((child) => [child.id, child]))
  const collect = (ids: string[], visited = new Set<string>()): Set<string> => {
    ids.forEach((childId) => {
      if (visited.has(childId)) return
      visited.add(childId)
      collect(children.get(childId)?.processes.flatMap((process) => process.subassemblyId ? [process.subassemblyId] : []) ?? [], visited)
    })
    return visited
  }
  const removed = collect([id])
  // A descendant reused by a surviving parent remains; only exclusively-owned descendants go.
  const survivingLinks = [input.processes, ...input.subassemblies.filter((child) => !removed.has(child.id)).map((child) => child.processes)]
    .flat().flatMap((process) => process.subassemblyId && process.subassemblyId !== id ? [process.subassemblyId] : [])
  collect(survivingLinks).forEach((childId) => { if (childId !== id) removed.delete(childId) })
  const unlink = (processes: ProcessInput[]) => processes.filter((process) => !process.subassemblyId || !removed.has(process.subassemblyId))
  return reconcileSubassemblyQuantities({
    ...input, processes: unlink(input.processes),
    subassemblies: input.subassemblies.filter((child) => !removed.has(child.id)).map((child) => ({ ...child, processes: unlink(child.processes) })),
  })
}

export function validSubassemblyLinkTargets(children: SubassemblyInput[], ownerId: string): SubassemblyInput[] {
  const byId = new Map(children.map((child) => [child.id, child]))
  const reachesOwner = (id: string, seen = new Set<string>()): boolean => {
    if (id === ownerId) return true
    if (seen.has(id)) return false
    seen.add(id)
    return byId.get(id)?.processes.some((process) => process.subassemblyId && reachesOwner(process.subassemblyId, seen)) ?? false
  }
  return children.filter((child) => !reachesOwner(child.id))
}
