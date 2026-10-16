import { Blob as NodeBlob } from "node:buffer"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ReportRenderer } from "../ReportRenderer"
import DiffReport from "./DiffReport"

const api = vi.hoisted(() => ({
  createModel: vi.fn(),
  createDiffEditor: vi.fn(),
  setTheme: vi.fn(),
  EditorOption: { ariaLabel: 8 },
}))
vi.mock("monaco-editor/editor/editor.api.js", () => ({ editor: api }))
vi.mock("monaco-editor/editor/editor.worker.js?worker", () => ({
  default: class {},
}))
const payload = {
  original: "before\n<script>literal</script>",
  modified: "after\n\u03bb",
}
let models: { dispose: ReturnType<typeof vi.fn> }[]
let editor: {
  setModel: ReturnType<typeof vi.fn>
  dispose: ReturnType<typeof vi.fn>
  getOriginalEditor: ReturnType<typeof vi.fn>
  getModifiedEditor: ReturnType<typeof vi.fn>
}
const originalEditor = {
  updateOptions: vi.fn(),
  getOption: vi.fn(),
  onDidChangeConfiguration: vi.fn(),
}
const modifiedEditor = {
  updateOptions: vi.fn(),
  getOption: vi.fn(),
  onDidChangeConfiguration: vi.fn(),
}

describe("lazy read-only Monaco report diff", () => {
  beforeEach(() => {
    vi.stubGlobal("Blob", NodeBlob)
    vi.resetAllMocks()
    models = []
    for (const inner of [originalEditor, modifiedEditor])
      inner.onDidChangeConfiguration.mockReturnValue({ dispose: vi.fn() })
    editor = {
      setModel: vi.fn(),
      dispose: vi.fn(),
      getOriginalEditor: vi.fn(() => originalEditor),
      getModifiedEditor: vi.fn(() => modifiedEditor),
    }
    api.createModel.mockImplementation(() => {
      const model = { dispose: vi.fn() }
      models.push(model)
      return model
    })
    api.createDiffEditor.mockReturnValue(editor)
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    document.documentElement.classList.remove("dark")
  })
  it("passes exact plain text into two immutable models and exposes a plain-text alternative", async () => {
    const view = render(
      <DiffReport
        title="Changes"
        content={new Blob([JSON.stringify(payload)])}
      />
    )
    await waitFor(() => expect(api.createDiffEditor).toHaveBeenCalledOnce())
    expect(api.createModel).toHaveBeenNthCalledWith(
      1,
      payload.original,
      "plaintext"
    )
    expect(api.createModel).toHaveBeenNthCalledWith(
      2,
      payload.modified,
      "plaintext"
    )
    expect(api.createDiffEditor).toHaveBeenCalledWith(
      expect.any(HTMLDivElement),
      expect.objectContaining({
        readOnly: true,
        domReadOnly: true,
        editContext: false,
        originalEditable: false,
        links: false,
        accessibilitySupport: "on",
        originalAriaLabel: "Changes: original text",
        modifiedAriaLabel: "Changes: modified text",
      })
    )
    expect(editor.setModel).toHaveBeenCalledWith({
      original: models[0],
      modified: models[1],
    })
    expect(originalEditor.updateOptions).toHaveBeenCalledWith({
      ariaLabel: "Changes: original text",
    })
    expect(modifiedEditor.updateOptions).toHaveBeenCalledWith({
      ariaLabel: "Changes: modified text",
    })
    const onConfiguration =
      originalEditor.onDidChangeConfiguration.mock.calls[0]![0]
    originalEditor.getOption.mockReturnValue("")
    act(() => onConfiguration({ hasChanged: (option: number) => option === 8 }))
    expect(originalEditor.updateOptions).toHaveBeenCalledTimes(2)
    fireEvent.click(screen.getByText("Plain-text diff alternatives"))
    expect(
      screen.getByText(payload.original, { normalizer: (text) => text })
    ).toHaveTextContent("<script>literal</script>")
    expect(document.querySelector("script")).toBeNull()
    act(() => document.documentElement.classList.add("dark"))
    await waitFor(() => expect(api.setTheme).toHaveBeenCalledWith("vs-dark"))
    view.unmount()
    expect(editor.dispose).toHaveBeenCalledOnce()
    expect(models.every((model) => model.dispose.mock.calls.length === 1)).toBe(
      true
    )
  })
  it.each([
    "{",
    "{}",
    '{"original":42,"modified":"x"}',
    '{"original":"x","modified":"y","language":"javascript"}',
  ])("rejects malformed diff contracts: %s", async (text) => {
    render(<DiffReport title="Bad" content={new Blob([text])} />)
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "original and modified text strings"
    )
    expect(api.createDiffEditor).not.toHaveBeenCalled()
  })
  it("disposes a partially initialized model when a later allocation fails", async () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => undefined)
    api.createModel
      .mockImplementationOnce(() => {
        const model = { dispose: vi.fn() }
        models.push(model)
        return model
      })
      .mockImplementationOnce(() => {
        throw new Error("Model allocation failed.")
      })
    render(
      <ReportRenderer
        kind="diff"
        title="Bad allocation"
        content={new Blob([JSON.stringify(payload)])}
      />
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Model allocation failed."
    )
    expect(models[0]!.dispose).toHaveBeenCalledOnce()
    error.mockRestore()
  })
})
