import { useEffect, useRef, useState } from 'react'
import { aiApi, vehicleApi } from '@/api'
import { useUI } from '@/components/UIProvider'
import Icon from '@/components/Icon'

/**
 * AI 生图面板（车辆图片的第二种来源）。
 *
 * 对接本机 ComfyUI 桌面版：用户选好自己的工作流模板（back/ComfyWorkflows/*.json），
 * 后端按模板实际节点 + ComfyUI object_info 定义动态生成表单 schema，
 * 本面板照单渲染——模板里有哪些可填输入（模型 / LoRA / 步数 / CFG / 宽高 /
 * 多区提示词 / shift……）就出现哪些控件，不挑工作流、不挑模型架构。
 * 图生图额外上传一张参考图（后端自动覆写 LoadImage 节点）。
 *
 * 「使用该图」的行为随调用场景而定：
 *  - 编辑已有车辆（editId 有值）：直接把图片 URL 写进车辆档案；
 *  - 新增车辆（editId 为空）：把成品拉成本地 File，走与「选择图片」
 *    完全相同的 pendingImages 通道，保存建档时一并落库。
 */

type Mode = 'text2img' | 'img2img'

/**
 * 从模板名推断适配模式：文生图模板名含「文生图 / text2img / t2i」，
 * 图生图模板名含「图生图 / img2img / i2i」；无法识别返回 null（用户自建模板）。
 * 面板打开/切模式时据此自动选中匹配的模板——否则按文件名排序，
 * 「Z-Image-Turbo_图生图.json」会排在文生图模板前面，文生图模式选到
 * 图生图模板，其 LoadImage 节点校验直接失败。
 */
function modeOfTemplate(name: string): Mode | null {
  const n = name.toLowerCase()
  if (n.includes('img2img') || n.includes('i2i') || name.includes('图生图')) return 'img2img'
  if (n.includes('text2img') || n.includes('t2i') || name.includes('文生图')) return 'text2img'
  return null
}

/** 后端 schema 的一个字段（DTOs/AiImageDtos.cs 的 AiFieldDef）。 */
interface FieldDef {
  key: string           // 补丁键："{节点id}.{输入名}"
  label: string
  group: string         // prompt / model / size / sampler / image / output / other
  kind: string          // text / textarea / int / float / select / seed / boolean
  options: string[]
  default: string | number | boolean | null
  min?: number
  max?: number
  step?: number
  required: boolean
  nodeType: string
  inputName: string
}

interface Schema {
  template: string
  fields: FieldDef[]
  notice?: string
}

/** 分组顺序与中文名（与后端 GroupOf 对应）。 */
const GROUPS: Array<{ id: string; title: string }> = [
  { id: 'prompt', title: '提示词' },
  { id: 'model', title: '模型与 LoRA' },
  { id: 'size', title: '尺寸' },
  { id: 'sampler', title: '采样参数' },
  { id: 'image', title: '图片' },
  { id: 'output', title: '输出' },
  { id: 'other', title: '其他参数' },
]

/** 尺寸预设：一键写进模板的 width / height 字段（存在才显示）。 */
const SIZE_PRESETS = [
  { label: '1024 × 1024（方图）', w: 1024, h: 1024 },
  { label: '832 × 1216（竖图）', w: 832, h: 1216 },
  { label: '1216 × 832（横图）', w: 1216, h: 832 },
  { label: '768 × 768', w: 768, h: 768 },
  { label: '512 × 512（SD1.5）', w: 512, h: 512 },
]

interface Props {
  /** 编辑中的车辆 id；null = 新增车辆建档流程 */
  editId: number | null
  /** 应用生成结果（编辑态传 URL，新增态由内部转 File 后回调） */
  onUse: (url: string, file?: File) => void
}

export default function AiImagePanel({ editId, onUse }: Props) {
  const { toast } = useUI()
  const [open, setOpen] = useState(false)
  const [mode, setMode] = useState<Mode>('text2img')

  // ComfyUI 状态
  const [online, setOnline] = useState<boolean | null>(null)
  const [offlineMsg, setOfflineMsg] = useState('')
  const [templates, setTemplates] = useState<string[]>([])
  const [models, setModels] = useState<string[]>([])

  // 模板 + schema 驱动的动态表单
  const [template, setTemplate] = useState('')
  const [schema, setSchema] = useState<Schema | null>(null)
  const [schemaLoading, setSchemaLoading] = useState(false)
  const [schemaError, setSchemaError] = useState('')
  const [values, setValues] = useState<Record<string, string>>({})
  // schema 拉取失败时的降级表单（后端仍走固定字段兜底通道）
  const [fbPrompt, setFbPrompt] = useState('')
  const [fbSeed, setFbSeed] = useState('')
  const [fbModel, setFbModel] = useState('')

  const [refFile, setRefFile] = useState<File | null>(null)
  const refInputRef = useRef<HTMLInputElement | null>(null)

  // 生成过程与结果
  const [generating, setGenerating] = useState(false)
  const [result, setResult] = useState<{ url: string; seed: number; elapsedMs: number } | null>(null)
  // 实时进度：后端 ComfyUI WebSocket → 进度跟踪器 → 本面板每秒轮询渲染进度条
  const [progress, setProgress] = useState<{ pct: number; stage: string } | null>(null)
  const progressTimer = useRef<number | null>(null)
  // 大图预览层
  const [previewOpen, setPreviewOpen] = useState(false)

  useEffect(() => {
    if (!open) return
    (async () => {
      try {
        const res: any = await aiApi.status()
        const d = res.data || {}
        setOnline(!!d.online)
        setOfflineMsg(d.message || '')
        setTemplates(d.templates || [])
        const all = [...(d.checkpoints || []).map((m: string) => ({ m, kind: 'ckpt' })),
          ...(d.diffusionModels || []).map((m: string) => ({ m, kind: 'gguf' }))]
        setModels(all.map(x => x.m))
        // 自动选中与当前模式匹配的模板（文生图模式选文生图模板，反之亦然）；
        // 没有可识别的匹配项才退回列表第一个
        const list: string[] = d.templates || []
        if (!template && list.length) {
          setTemplate(list.find(t => modeOfTemplate(t) === mode) ?? list[0])
        }
      } catch (e: any) {
        setOnline(false)
        setOfflineMsg(e?.message || '状态查询失败')
      }
    })()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  // 选中模板 → 拉 schema → 按字段默认值初始化表单
  useEffect(() => {
    if (!template) { setSchema(null); setValues({}); return }
    let cancelled = false
    setSchemaLoading(true)
    setSchemaError('')
    setResult(null)
    ;(async () => {
      try {
        const res: any = await aiApi.schema(template)
        if (cancelled) return
        const s: Schema = res.data || { template, fields: [] }
        setSchema(s)
        const init: Record<string, string> = {}
        for (const f of s.fields || []) {
          // 种子默认 -1（随机），让每次生成结果不同；其余用模板当前值预填
          if (f.kind === 'seed') init[f.key] = '-1'
          else if (f.default !== null && f.default !== undefined) init[f.key] = String(f.default)
        }
        setValues(init)
      } catch (e: any) {
        if (cancelled) return
        setSchema(null)
        setValues({})
        setSchemaError(e?.message || '模板解析失败')
      } finally {
        if (!cancelled) setSchemaLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [template])

  const fields = schema?.fields ?? []
  const widthField = fields.find(f => f.inputName === 'width')
  const heightField = fields.find(f => f.inputName === 'height')
  const promptField = fields.find(f => f.required)

  // 模式切换：当前模板不适配新模式时，自动换到适配的模板（识别不出模式的
  // 自建模板不换，由用户自己决定）。防止文生图模式下图生图模板的
  // LoadImage 校验失败，或图生图模式下模板没有 LoadImage 导致参考图被忽略。
  useEffect(() => {
    if (!template || !templates.length) return
    if (modeOfTemplate(template) === mode) return
    const match = templates.find(t => modeOfTemplate(t) === mode)
    if (match) setTemplate(match)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode])

  // 面板关闭/卸载时停掉轮询，避免关了还在打接口
  useEffect(() => () => {
    if (progressTimer.current) { clearInterval(progressTimer.current); progressTimer.current = null }
  }, [])

  const setValue = (key: string, v: string) => setValues(prev => ({ ...prev, [key]: v }))

  const generate = async () => {
    if (!template) { toast('请先选择工作流模板', 'warning'); return }
    if (mode === 'img2img' && !refFile) { toast('图生图需要先上传参考图', 'warning'); return }

    // 组装请求：动态字段（非空才带，留空 = 用模板原值）+ 提示词必填校验
    const inputs: Record<string, string> = {}
    for (const f of fields) {
      if (mode === 'img2img' && f.group === 'image') continue // 参考图由上传覆写
      const v = (values[f.key] ?? '').trim()
      if (v) inputs[f.key] = v
    }
    const prompt = promptField ? (values[promptField.key] ?? '').trim() : ''
    const hasInputs = Object.keys(inputs).length > 0

    if (fields.length > 0) {
      if (promptField && !prompt) { toast(`请填写${promptField.label}`, 'warning'); return }
    } else {
      if (!fbPrompt.trim()) { toast('请填写提示词', 'warning'); return }
    }

    setGenerating(true)
    setResult(null)
    setProgress({ pct: 0, stage: '已提交，等待开始' })

    // jobId 串起「提交」与「轮询」：后端凭它把 ComfyUI 的实时进度
    // （WebSocket：排队中/执行节点/采样步数/失败原因）写给前端
    // crypto.randomUUID() 在 HTTP（非 localhost）环境下不存在，需要 fallback
    const jobId = (typeof crypto !== 'undefined' && crypto.randomUUID)
      ? crypto.randomUUID()
      : `${Date.now()}-${Math.random().toString(36).slice(2, 11)}`
    const stopPolling = () => {
      if (progressTimer.current) { clearInterval(progressTimer.current); progressTimer.current = null }
    }
    stopPolling()
    progressTimer.current = window.setInterval(async () => {
      try {
        const res: any = await aiApi.progress(jobId)
        const p = res.data
        if (!p) return
        if (p.state === 'running') setProgress({ pct: p.percent || 0, stage: p.stage || '' })
        else if (p.state === 'error') stopPolling() // 失败：POST 那边会抛同样的错并提示
      } catch { /* 轮询失败不打断生成，POST 仍是最终依据 */ }
    }, 1000)

    try {
      const common = fields.length > 0
        ? {
            template,
            prompt,
            jobId,
            inputs: hasInputs ? inputs : undefined,
          }
        : {
            template,
            prompt: fbPrompt.trim(),
            seed: fbSeed ? Number(fbSeed) : -1,
            model: fbModel || undefined,
            jobId,
          }
      const res: any = mode === 'img2img'
        ? await aiApi.img2img(common as any, refFile!)
        : await aiApi.text2img(common as any)
      const d = res.data || {}
      setResult({ url: d.url, seed: d.seed, elapsedMs: d.elapsedMs })
      toast(`生成成功（${((d.elapsedMs || 0) / 1000).toFixed(1)}s）`, 'success')
    } catch (e: any) {
      toast(e?.message || '生成失败', 'error')
    } finally {
      stopPolling()
      setProgress(null)
      setGenerating(false)
    }
  }

  const useResult = async () => {
    if (!result) return
    if (editId) {
      try {
        await vehicleApi.setImageUrl(editId, result.url)
        onUse(result.url)
        toast('已设为车辆图片', 'success')
      } catch (e: any) {
        toast(e?.message || '设置失败', 'error')
      }
      return
    }
    // 新增建档：把成品图拉成本地 File，与「选择图片」走同一条落库通道
    try {
      const resp = await fetch(result.url)
      const blob = await resp.blob()
      const file = new File([blob], `ai-${Date.now()}.png`, { type: blob.type || 'image/png' })
      onUse(result.url, file)
      toast('已加入待保存图片，保存建档后生效', 'success')
    } catch {
      toast('图片拉取失败，请重试生成', 'error')
    }
  }

  /** 渲染一个 schema 字段（按 kind 选控件）。 */
  const renderField = (f: FieldDef) => {
    const v = values[f.key] ?? ''
    const disabled = generating
    const ph = f.kind === 'seed'
      ? '-1 随机；填整数可复现'
      : (f.default !== null && f.default !== undefined ? `模板默认：${f.default}` : '')

    if (f.kind === 'select' || f.kind === 'boolean') {
      const missing = f.default !== null && f.default !== undefined &&
        f.options.length > 0 && !f.options.includes(String(f.default))
      return (
        <div className="form-group" key={f.key}>
          <label>{f.label}{f.required && <em className="ai-req">*</em>}</label>
          <select value={v} onChange={e => setValue(f.key, e.target.value)} disabled={disabled}>
            <option value="">{f.options.length ? '用模板默认' : '（暂无可选项，保持模板原值）'}</option>
            {/* 选项名可能跨加载器类型重复（同一文件既是 ckpt 又是 unet），key 必须带下标兜底 */}
            {f.options.map((o, i) => <option key={`${o}-${i}`} value={o}>{o}</option>)}
          </select>
          {missing && <div className="ai-field-warn">模板当前值「{String(f.default)}」不在可选项里，请改选</div>}
        </div>
      )
    }
    if (f.kind === 'textarea') {
      return (
        <div className="form-group" key={f.key}>
          <label>{f.label}{f.required && <em className="ai-req">*</em>}</label>
          <textarea rows={2} value={v} onChange={e => setValue(f.key, e.target.value)}
            placeholder={ph} disabled={disabled} />
        </div>
      )
    }
    // text / int / float / seed 都走输入框，只做字符过滤
    const filter = f.kind === 'seed' ? /[^\d-]/g
      : f.kind === 'float' ? /[^\d.-]/g
        : f.kind === 'int' ? /\D/g : null
    return (
      <div className="form-group" key={f.key}>
        <label>{f.label}{f.required && <em className="ai-req">*</em>}</label>
        <input
          value={v}
          onChange={e => setValue(f.key, filter ? e.target.value.replace(filter, '') : e.target.value)}
          placeholder={ph}
          inputMode={f.kind === 'text' ? undefined : 'decimal'}
          disabled={disabled}
        />
      </div>
    )
  }

  if (!open) {
    return (
      <button className="ai-entry" onClick={() => setOpen(true)}>
        <Icon name="image" size={14} color="#fff" />
        AI 生成图片
      </button>
    )
  }

  return (
    <div className="ai-panel">
      <div className="ai-panel-head">
        <span className="ai-panel-title">
          <Icon name="image" size={15} />
          AI 生成图片
          <em>ComfyUI 桌面版</em>
        </span>
        <button className="ai-panel-close" title="收起" onClick={() => setOpen(false)}>
          <Icon name="close" size={14} />
        </button>
      </div>

      {online === false && (
        <div className="ai-offline">
          <Icon name="warning" size={15} />
          <span>{offlineMsg || 'ComfyUI 未启动'}</span>
        </div>
      )}

      <div className="ai-mode-tabs">
        <button className={mode === 'text2img' ? 'active' : ''} onClick={() => setMode('text2img')}>文生图</button>
        <button className={mode === 'img2img' ? 'active' : ''} onClick={() => setMode('img2img')}>图生图</button>
      </div>

      <div className="ai-grid">
        <div className="form-group ai-template">
          <label>工作流模板</label>
          <select value={template} onChange={e => setTemplate(e.target.value)} disabled={generating}>
            <option value="">{templates.length ? '请选择模板' : '（无模板，见 ComfyWorkflows/README.txt）'}</option>
            {templates.map(t => {
              const m = modeOfTemplate(t)
              return <option key={t} value={t}>{m ? `${t}（${m === 'img2img' ? '图生图' : '文生图'}）` : t}</option>
            })}
          </select>
          {template && modeOfTemplate(template) && modeOfTemplate(template) !== mode && (
            <div className="ai-hint">
              当前模板是{modeOfTemplate(template) === 'img2img' ? '图生图' : '文生图'}模板，与{mode === 'img2img' ? '图生图' : '文生图'}模式不匹配，可能无法生成，建议切换模式或更换模板。
            </div>
          )}
          {mode === 'img2img' && template && fields.length > 0 && !fields.some(f => f.group === 'image') && (
            <div className="ai-hint">当前模板没有 LoadImage 节点，上传的参考图不会生效，请换用图生图模板。</div>
          )}
        </div>
      </div>

      {schemaLoading && <div className="ai-hint">正在按模板节点生成表单…</div>}
      {schema?.notice && <div className="ai-hint">{schema.notice}</div>}

      {/* ===== schema 驱动的动态表单 ===== */}
      {fields.length > 0 && GROUPS.map(g => {
        const gf = fields.filter(f => f.group === g.id && !(mode === 'img2img' && g.id === 'image'))
        if (!gf.length) return null
        return (
          <div className="ai-group" key={g.id}>
            <div className="ai-group-title">{g.title}</div>
            <div className="ai-grid">
              {g.id === 'size' && widthField && heightField && (
                <div className="form-group">
                  <label>尺寸预设</label>
                  <select
                    value=""
                    disabled={generating}
                    onChange={e => {
                      const p = SIZE_PRESETS[Number(e.target.value)]
                      if (p) {
                        setValue(widthField.key, String(p.w))
                        setValue(heightField.key, String(p.h))
                      }
                    }}
                  >
                    <option value="">选择预设快速填写</option>
                    {SIZE_PRESETS.map((p, i) => <option key={p.label} value={i}>{p.label}</option>)}
                  </select>
                </div>
              )}
              {gf.map(renderField)}
            </div>
          </div>
        )
      })}

      {/* ===== 图生图参考图上传 ===== */}
      {mode === 'img2img' && (
        <div className="form-group">
          <label>参考图</label>
          <div className="ai-ref-row">
            <button className="btn-sm btn-sm-default" onClick={() => refInputRef.current?.click()} disabled={generating}>
              <Icon name="upload" size={13} />{refFile ? '更换参考图' : '选择参考图'}
            </button>
            {refFile && <span className="ai-ref-name" title={refFile.name}>{refFile.name}</span>}
            <input
              ref={refInputRef}
              type="file"
              accept="image/jpeg,image/png,image/webp,image/gif"
              style={{ display: 'none' }}
              onChange={e => setRefFile(e.target.files?.[0] || null)}
            />
          </div>
        </div>
      )}

      {/* ===== schema 拉取失败 / 模板无schema：降级表单（后端固定字段兜底通道） ===== */}
      {template && fields.length === 0 && !schemaLoading && (
        <div className="ai-group">
          <div className="ai-group-title">基础参数</div>
          {schemaError && <div className="ai-hint">表单生成失败（{schemaError}），请用基础参数生成，或检查模板文件。</div>}
          <div className="form-group">
            <label>提示词（正向）</label>
            <textarea rows={2} value={fbPrompt} onChange={e => setFbPrompt(e.target.value)}
              placeholder="描述你想要的车辆图片，如：a red electric scooter, clean white background, product photo"
              disabled={generating} />
          </div>
          <div className="ai-grid">
            <div className="form-group">
              <label>种子（-1 随机）</label>
              <input value={fbSeed} onChange={e => setFbSeed(e.target.value.replace(/[^\d-]/g, ''))}
                placeholder="-1" disabled={generating} />
            </div>
          <div className="form-group">
            <label>主模型（可选）</label>
            <select value={fbModel} onChange={e => setFbModel(e.target.value)} disabled={generating}>
              <option value="">用模板默认</option>
              {/* checkpoints 与 diffusionModels 可能有同名文件，key 带下标防重 */}
              {models.map((m, i) => <option key={`${m}-${i}`} value={m}>{m}</option>)}
            </select>
          </div>
          </div>
        </div>
      )}

      <div className="ai-actions">
        <button className="btn-sm btn-sm-primary" onClick={generate} disabled={generating || !online || !template}>
          <Icon name={generating ? 'refresh' : 'check'} size={14} color="#fff" />
          {generating ? '生成中…（本地推理，请耐心等待）' : '开始生成'}
        </button>
        {result && (
          <button className="btn-sm btn-sm-default" onClick={() => setResult(null)} disabled={generating}>
            <Icon name="close" size={13} />清除结果
          </button>
        )}
      </div>

      {generating && (
        <div className="ai-progress">
          <div className="ai-progress-head">
            <Icon name="refresh" size={13} />
            <span>{progress?.stage || '生成中'}</span>
            <b>{progress?.pct ?? 0}%</b>
          </div>
          <div className="ai-progress-bar">
            <i style={{ width: `${Math.max(3, progress?.pct ?? 0)}%` }} />
          </div>
          <div className="ai-hint">本机显卡推理，一张图通常需要几十秒到几分钟，请勿关闭面板。</div>
        </div>
      )}

      {result && (
        <div className="ai-result">
          <img src={result.url} alt="AI 生成结果" onClick={() => setPreviewOpen(true)} title="点击查看原图" />
          <div className="ai-result-meta">
            <div>种子 {result.seed} · 耗时 {(result.elapsedMs / 1000).toFixed(1)}s</div>
            <button className="btn-sm btn-sm-primary" onClick={useResult}>
              <Icon name="check" size={13} color="#fff" />使用该图
            </button>
          </div>
        </div>
      )}

      {previewOpen && result && (
        <div className="ai-preview-overlay" onClick={() => setPreviewOpen(false)}>
          <div className="ai-preview-close" onClick={() => setPreviewOpen(false)}>✕</div>
          <img src={result.url} alt="预览" onClick={e => e.stopPropagation()} />
        </div>
      )}
    </div>
  )
}
