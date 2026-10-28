import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useUI } from '@/components/UIProvider'
import { configApi, logApi } from '@/api'
import Icon from '@/components/Icon'
import Pagination from '@/components/Pagination'
import PayConfigPanel from './components/PayConfigPanel'
import { fmtDT, recentDateRange } from '@/utils/datetime'

/** 参数说明补充：用于把 config_key 翻译成更易懂的中文名 */
const CONFIG_LABEL: Record<string, string> = {
  daily_rent: '日租金',
  deposit: '默认押金',
  overdue_multiplier: '超时租金倍数',
  max_login_fail: '单IP最大登录失败次数',
  lock_minutes: 'IP锁定时长（分钟）',
  maintain_cycle_days: '保养周期（天）',
  member_default_price: '会员默认价格',
}

// 核心业务参数：后端受保护不可删除，前端据此隐藏删除按钮（与后端 NumericKeys 对应）。
// monthly_rent / overdue_rate 已随不参与计费的配置一并移除（计费按小时租金，逾期费按倍数）。
const CORE_KEYS = ['daily_rent', 'deposit', 'overdue_multiplier', 'max_login_fail', 'lock_minutes', 'maintain_cycle_days', 'member_default_price']

/**
 * 日志筛选初始值：日期默认带出**近 30 天**（含今天），其余条件为空。
 *
 * 留空会让两个日期框只显示「年/月/日」占位，看不出查的是哪段时间，
 * 也不符合需求 6.1「界面布局清晰、无冗余操作」——与报表页 `makeDefaultRange()` 同一口径，
 * 两个查询页面的时间段语义保持一致。日期框仍可改、可清空，清空即查全部历史。
 */
const DEFAULT_LOG_DAYS = 30

const makeLogFilter = () => {
  const range = recentDateRange(DEFAULT_LOG_DAYS)
  return {
    // 关键字同时匹配「操作人（姓名/账号）」与「操作内容」——用姓名搜索即可定位到某个人，
    // 不必再记住内部数字用户 ID（这就是把「操作 ID」换成「操作人」的落点）。
    keyword: '',
    from: range.from,
    to: range.to,
    module: '',
    result: '',
  }
}
type LogFilter = ReturnType<typeof makeLogFilter>

/**
 * 系统配置页。
 *
 * 结构图「系统管理模块」下含「权限管理 / 系统配置 / 日志与备份」三个子项，
 * 而需求 4.2.1「系统配置管理」明确包含「支付通道参数」，
 * 因此原独立页 支付配置（/pay-config）并入本页作为第二个页签，与结构图对齐。
 * 旧链接 /pay-config 仍可用（路由重定向到 /config?tab=pay）。
 */
type TabKey = 'config' | 'pay' | 'log'

export default function SystemConfig() {
  const { toast, confirm } = useUI()
  const [searchParams] = useSearchParams()
  const [tab, setTab] = useState<TabKey>(() => {
    const t = searchParams.get('tab')
    return t === 'pay' || t === 'log' ? (t as TabKey) : 'config'
  })
  /** 从余额卡「去绑定」跳过来时带的渠道（alipay / wechat / bank），用于展开对应配置卡 */
  const focusChannel = searchParams.get('channel') || undefined
  const [list, setList] = useState<any[]>([])
  const [logs, setLogs] = useState<any[]>([])
  const [page, setPage] = useState(1)
  // 日志筛选（需求 4.2.1）：条件一变即自动查询，不再需要点「查询」——
  // 与订单/结算等页保持一致，输入即生效。
  const [logTotal, setLogTotal] = useState(0)
  const [logModules, setLogModules] = useState<string[]>([])
  const [logFilter, setLogFilter] = useState<LogFilter>(makeLogFilter)
  /** 每页条数（可调，默认 20） */
  const [logPageSize, setLogPageSize] = useState(20)
  /** 最近一次成功拉取日志的时刻（给「实时更新」一个可见反馈） */
  const [logRefreshedAt, setLogRefreshedAt] = useState<Date | null>(null)

  // 默认筛选（近 30 天）冻结一份：用于判断「是否已改动」以及重置
  const defaultLogFilter = useMemo<LogFilter>(makeLogFilter, [])
  /**
   * 参数编辑改成「表格里直接改 + 批量保存」。
   *
   * 原来每改一项要走「点修改 → 输入 → 确认弹窗 → 等列表刷新」四步，
   * 调价时往往要同时改日租金、押金、超时倍数，等于来回三趟。
   * 现在值单元格恒为输入框，改动先攒在 drafts 里（键 = configKey），
   * 底部浮动条显示「已改 N 项」，一次确认、一次保存。
   */
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [savingCfg, setSavingCfg] = useState(false)
  const [loading, setLoading] = useState(true)
  const [backingUp, setBackingUp] = useState(false)
  const [resetting, setResetting] = useState(false)
  // 备份文件列表：加密 .erbk，可下载/回滚。恢复是高危操作，按文件名逐个确认
  const [backups, setBackups] = useState<any[]>([])
  /** 备份文件列表改为弹窗查看（不单独开页面，也不挤在日志表格上方） */
  const [backupOpen, setBackupOpen] = useState(false)
  const [restoring, setRestoring] = useState('')

  // ComfyUI 地址（AI 生图）：当前生效值 + 来源 + 在线状态，页面可直接改、可先测再存
  const [comfy, setComfy] = useState<any>(null)
  const [comfyUrl, setComfyUrl] = useState('')
  const [comfySaving, setComfySaving] = useState(false)
  const [comfyTesting, setComfyTesting] = useState(false)

  const loadComfy = async () => {
    try {
      const res: any = await configApi.comfyuiGet()
      setComfy(res.data || null)
      setComfyUrl(res.data?.url || '')
    } catch (e: any) { toast(e?.message || 'ComfyUI 配置读取失败', 'error') }
  }

  const handleComfySave = async () => {
    const url = comfyUrl.trim()
    if (!url) { toast('请填写 ComfyUI 服务地址', 'warning'); return }
    setComfySaving(true)
    try {
      await configApi.comfyuiSet(url)
      toast('ComfyUI 地址已保存，即时生效', 'success')
      loadComfy()
    } catch (e: any) { toast(e?.message || '保存失败', 'error') }
    finally { setComfySaving(false) }
  }

  const handleComfyTest = async () => {
    const url = comfyUrl.trim()
    if (!url) { toast('请先填写要测试的地址', 'warning'); return }
    setComfyTesting(true)
    try {
      const res: any = await configApi.comfyuiTest(url)
      const d = res.data || {}
      toast(res?.message || (d.ok ? '连接成功：ComfyUI 在线' : '连接失败'), d.ok ? 'success' : 'error')
      loadComfy()
    } catch (e: any) { toast(e?.message || '测试失败', 'error') }
    finally { setComfyTesting(false) }
  }

  const loadConfig = async () => {
    try {
      const res: any = await configApi.list()
      setList(res.data || [])
    } catch (e: any) { toast(e?.message || '系统配置加载失败', 'error') }
  }

  /**
   * 拉取日志。
   * @param p 页码
   * @param silent true = 后台静默刷新（不显示 loading、不弹错误 toast），供定时轮询使用，
   *               避免每 15 秒闪一下加载态把正在看的内容晃走。
   */
  const loadLogs = async (p = page, silent = false) => {
    if (!silent) setLoading(true)
    try {
      const res: any = await logApi.list({
        page: p,
        size: logPageSize,
        from: logFilter.from || undefined,
        to: logFilter.to || undefined,
        module: logFilter.module || undefined,
        result: logFilter.result || undefined,
        keyword: logFilter.keyword.trim() || undefined,
      })
      setLogs(res.data?.items || [])
      setLogTotal(res.data?.total ?? 0)
      setLogRefreshedAt(new Date())
    } catch (e: any) { if (!silent) toast(e?.message || '日志加载失败', 'error') }
    finally { if (!silent) setLoading(false) }
  }

  /** 手动刷新：给出可见反馈（toast），避免「点了刷新没反应」的错觉 */
  const handleRefreshLogs = async () => {
    await loadLogs(page)
    toast(`日志已刷新 · ${new Date().toLocaleTimeString('zh-CN', { hour12: false })}`, 'success')
  }

  // 模块下拉选项取日志里实际出现过的值，避免前端硬编码模块名与后端漂移
  const loadLogModules = () => {
    logApi.modules().then((r: any) => setLogModules(r.data || [])).catch(() => setLogModules([]))
  }

  /** 改动任一筛选条件 → 立即生效并回到第 1 页（无需再点「查询」） */
  const patchLogFilter = (patch: Partial<LogFilter>) => {
    setLogFilter(prev => ({ ...prev, ...patch }))
    setPage(1)
  }

  /** 重置 = 回到默认条件（日期回到近 30 天），而不是清成空区间 */
  const resetLogFilter = () => {
    setLogFilter(makeLogFilter())
    setPage(1)
  }

  /** 是否有非默认筛选（决定是否显示「重置」图标） */
  const hasLogFilter = useMemo(() => {
    const d = defaultLogFilter
    return logFilter.keyword.trim() !== ''
      || logFilter.module !== ''
      || logFilter.result !== ''
      || logFilter.from !== d.from
      || logFilter.to !== d.to
  }, [logFilter, defaultLogFilter])

  useEffect(() => { loadConfig(); loadComfy() }, [])

  // 进入日志页：加载下拉选项与备份数（各一次）
  useEffect(() => {
    if (tab === 'log') { loadLogModules(); loadBackups() }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab])

  // 筛选条件 / 页码 / 每页条数变化 → 自动查询（不再依赖「查询」按钮）
  useEffect(() => {
    if (tab === 'log') loadLogs(page)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, page, logFilter, logPageSize])

  // 实时更新：停留在日志页时每 15 秒静默拉取一次，新日志自动出现
  useEffect(() => {
    if (tab !== 'log') return
    const id = window.setInterval(() => loadLogs(page, true), 15000)
    return () => window.clearInterval(id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, page, logFilter, logPageSize])

  const dirtyKeys = Object.keys(drafts)

  /** 改动写进草稿；改回原值即视为没改（自动撤销这一项） */
  const setDraft = (c: any, v: string) => {
    setDrafts(prev => {
      const next = { ...prev }
      if (v === String(c.configValue)) delete next[c.configKey]
      else next[c.configKey] = v
      return next
    })
  }

  const undoOne = (key: string) => setDrafts(prev => {
    const next = { ...prev }
    delete next[key]
    return next
  })

  /**
   * 批量保存：先整体校验再逐条提交。
   * 顺序上必须先校验完——否则前 3 项已落库、第 4 项才报错，留下半截状态更难收拾。
   */
  const saveDrafts = async () => {
    if (dirtyKeys.length === 0) return
    for (const key of dirtyKeys) {
      const v = drafts[key].trim()
      const label = CONFIG_LABEL[key] || key
      if (!v) { toast(`「${label}」不能为空`, 'warning'); return }
      if (Number.isNaN(Number(v)) || Number(v) < 0) { toast(`「${label}」需为不小于 0 的数字`, 'warning'); return }
    }
    const preview = dirtyKeys.slice(0, 4)
      .map(k => `「${CONFIG_LABEL[k] || k}」→ ${drafts[k].trim()}`).join('；')
    const ok = await confirm({
      title: `保存 ${dirtyKeys.length} 项参数`,
      message: `${preview}${dirtyKeys.length > 4 ? ` 等 ${dirtyKeys.length} 项` : ''}。修改后对新订单立即生效。`,
      type: 'question',
      confirmText: '确认保存',
    })
    if (!ok) return
    setSavingCfg(true)
    try {
      // 后端一次只接受一个 key，这里按草稿顺序逐条提交，全部成功后再清草稿并刷新
      for (const key of dirtyKeys) await configApi.update(key, drafts[key].trim())
      toast(`${dirtyKeys.length} 项参数已保存`, 'success')
      setDrafts({})
      loadConfig()
    } catch (e: any) { toast(e?.message || '保存失败', 'error') }
    finally { setSavingCfg(false) }
  }

  /**
   * 数据库初始化（需求 4.2.1 运维扩展）：终版口径——清空业务流水、会员类型、
   * 数据字典与管理员以外的账号；管理员账号、系统配置（业务参数）与支付配置保留。
   */
  /** 删除管理员自建的配置项（核心业务参数无此按钮，后端亦受保护） */
  const handleDelete = async (c: any) => {
    const ok = await confirm({
      title: '删除配置项',
      message: `确定删除配置「${c.configKey}」（${CONFIG_LABEL[c.configKey] || c.configDesc || '自定义项'} = ${c.configValue}）吗？删除后不可恢复。`,
      type: 'warning',
      danger: true,
      confirmText: '确认删除',
    })
    if (!ok) return
    try {
      await configApi.remove(c.configKey)
      toast('配置项已删除', 'success')
      loadConfig()
    } catch (e: any) { toast(e?.message || '删除失败', 'error') }
  }

  const handleResetDb = async () => {
    const ok = await confirm({
      title: '数据库初始化',
      message: '将清空：订单、支付、结算、续租、维保、盘点、报废、操作日志、客户与车辆档案，以及会员类型、数据字典和管理员以外的登录账号。管理员账号、系统配置（租金、押金等参数）与支付配置完好保留。此操作不可恢复，建议先执行数据库备份！',
      type: 'warning',
      danger: true,
      confirmText: '我已知晓，执行初始化',
    })
    if (!ok) return
    setResetting(true)
    try {
      const res: any = await configApi.resetDb()
      toast(res?.message || '数据库初始化完成', 'success')
      // 系统配置在初始化中被完好保留，重新拉取即可——之前误用 setList([]) 清空，
      // 导致页面上配置瞬间「清零」，要手动刷新浏览器才恢复（实际数据从未丢过）
      loadConfig()
      loadLogs(1)
      setPage(1)
    } catch (e: any) { toast(e?.message || '初始化失败', 'error') }
    finally { setResetting(false) }
  }

  const handleBackup = async () => {
    const ok = await confirm({
      title: '数据库备份',
      message: '确定立即执行全库备份吗？备份文件将保存到服务器 backups 目录（AES-256-GCM 加密），包含全部 17 张业务表数据。',
      type: 'question',
      confirmText: '开始备份',
    })
    if (!ok) return
    setBackingUp(true)
    try {
      const res: any = await logApi.backup()
      const d = res?.data || {}
      const cost = d.elapsedMs < 1000 ? `${d.elapsedMs}ms` : `${(d.elapsedMs / 1000).toFixed(1)}s`
      toast(`备份成功：${d.fileName}（${d.sizeKB}KB，共 ${d.tableCount} 张表，已加密，耗时 ${cost}）`, 'success')
      loadLogs(1)
      setPage(1)
      loadBackups()
    } catch (e: any) { toast(e?.message || '备份失败', 'error') }
    finally { setBackingUp(false) }
  }

  const loadBackups = () => {
    logApi.backups().then((r: any) => setBackups(r.data || [])).catch(() => setBackups([]))
  }

  const handleDownloadBackup = async (fileName: string) => {
    try {
      const blob: any = await logApi.downloadBackup(fileName)
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = fileName
      a.click()
      URL.revokeObjectURL(url)
    } catch (e: any) { toast(e?.message || '下载失败', 'error') }
  }

  const handleRestore = async (fileName: string) => {
    const ok = await confirm({
      title: '用备份覆盖当前数据库',
      message: `将用 ${fileName} 覆盖当前全部业务数据（订单、支付、客户、车辆、日志等）。系统会先自动做一份"恢复前快照"，但仍建议在停业时段执行。`,
      type: 'warning',
      danger: true,
      confirmText: '覆盖恢复',
    })
    if (!ok) return
    setRestoring(fileName)
    try {
      const res: any = await logApi.restore(fileName)
      toast(res?.message || '恢复完成', 'success')
      loadLogs(1)
      setPage(1)
      loadBackups()
    } catch (e: any) { toast(e?.message || '恢复失败', 'error') }
    finally { setRestoring('') }
  }

  return (
    <div className={`card system-config-card${dirtyKeys.length > 0 ? ' has-cfg-bar' : ''}`}>
      <div className="card-title">
        <Icon name="config" size={18} chip />配置与日志
        {tab === 'log' && (
          <div className="toolbar-actions" style={{ marginLeft: 'auto' }}>
            <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={handleBackup} disabled={backingUp || resetting}>
              <Icon name={backingUp ? 'refresh' : 'save'} size={14} /><span className="btn-label">{backingUp ? '备份中…' : '数据库备份'}</span>
            </button>
            <button className="btn-sm btn-sm-default btn-icon-sm" onClick={() => { loadBackups(); setBackupOpen(true) }}>
              <Icon name="download" size={14} /><span className="btn-label">备份文件{backups.length > 0 ? `（${backups.length}）` : ''}</span>
            </button>
            <button className="btn-sm btn-sm-danger btn-icon-sm" onClick={handleResetDb} disabled={backingUp || resetting}>
              <Icon name={resetting ? 'refresh' : 'empty'} size={14} /><span className="btn-label">{resetting ? '初始化中…' : '数据库初始化'}</span>
            </button>
            <button className="btn-sm btn-sm-default btn-icon-sm" onClick={handleRefreshLogs}>
              <Icon name="refresh" size={14} /><span className="btn-label">刷新</span>
            </button>
          </div>
        )}
      </div>

      <div className="toolbar">
        <div className="tab-switch">
          <button className={`tab-btn ${tab === 'config' ? 'active' : ''}`} onClick={() => setTab('config')}>
            参数配置<span className="tab-count">{list.length}</span>
          </button>
          <button className={`tab-btn ${tab === 'pay' ? 'active' : ''}`} onClick={() => setTab('pay')}>
            支付配置
          </button>
          <button className={`tab-btn ${tab === 'log' ? 'active' : ''}`} onClick={() => setTab('log')}>
            操作日志
          </button>
        </div>
      </div>

      {tab === 'pay' && <PayConfigPanel focusChannel={focusChannel} />}

      {tab === 'config' && (
        <>
          <div className="callout callout-info">
            <Icon name="info" size={18} chip />
            <span>这里的日租金、押金、超时倍数等参数会直接参与订单费用的自动核算，修改后对新订单立即生效。</span>
          </div>

          {/* ===== ComfyUI 设置（AI 生图）：换端口/换机器后在此更新地址 ===== */}
          <div className="comfy-card">
            <div className="comfy-card-title">
              <Icon name="image" size={16} chip />
              ComfyUI 设置（AI 生图）
              {comfy && (
                <span className={`status-tag ${comfy.online ? 'status-success' : 'status-danger'}`}>
                  {comfy.online ? '在线' : '离线'}
                </span>
              )}
            </div>
            <div className="comfy-card-body">
              <div className="form-group">
                <label>服务地址</label>
                <input
                  value={comfyUrl}
                  onChange={e => setComfyUrl(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter') handleComfySave() }}
                  placeholder="http://127.0.0.1:8188"
                  disabled={comfySaving || comfyTesting}
                />
              </div>
              <div className="btn-group">
                <button className="btn-sm btn-sm-primary" onClick={handleComfySave} disabled={comfySaving || comfyTesting}>
                  <Icon name="save" size={13} />{comfySaving ? '保存中…' : '保存'}
                </button>
                <button className="btn-sm btn-sm-default" onClick={handleComfyTest} disabled={comfySaving || comfyTesting}>
                  <Icon name="refresh" size={13} />{comfyTesting ? '测试中…' : '测试连接'}
                </button>
              </div>
            </div>
            <div className="comfy-card-tips">
              当前生效地址：<b>{comfy?.url || '—'}</b>（{comfy?.source === 'db' ? '来自本页配置' : '出厂默认，尚未配置过'}）。
              ComfyUI 桌面版换端口、换机器或换 IP 后，在这里改成新地址保存即可，生图功能会自动连过去；
              不确定通不通，先点「测试连接」验一下再保存。
            </div>
          </div>
          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>参数名</th><th>参数含义</th><th className="num">参数值</th>
                  <th>配置说明</th><th>更新时间</th><th>操作</th>
                </tr>
              </thead>
              <tbody>
                {list.map(c => {
                  const dirty = c.configKey in drafts
                  return (
                    <tr key={c.configId} className={dirty ? 'cfg-row-dirty' : undefined}>
                      <td className="mono">{c.configKey}</td>
                      <td>{CONFIG_LABEL[c.configKey] || '—'}</td>
                      <td className="num">
                        <div className="cfg-value">
                          <input
                            className="cfg-input"
                            value={dirty ? drafts[c.configKey] : String(c.configValue)}
                            inputMode="decimal"
                            aria-label={`${CONFIG_LABEL[c.configKey] || c.configKey} 参数值`}
                            onChange={e => setDraft(c, e.target.value)}
                            onKeyDown={e => {
                              // 回车直接提交全部草稿（连续改多项时不用去找按钮）
                              if (e.key === 'Enter') saveDrafts()
                              if (e.key === 'Escape') undoOne(c.configKey)
                            }}
                          />
                          {dirty && (
                            <button className="cfg-undo" title="撤销这一项修改" onClick={() => undoOne(c.configKey)}>
                              <Icon name="refresh" size={12} />
                            </button>
                          )}
                        </div>
                      </td>
                      <td>{c.configDesc}</td>
                      <td className="cell-time">{fmtDT(c.updateTime)}</td>
                      <td>
                        {CORE_KEYS.includes(c.configKey)
                          ? <span className="cfg-core" title="核心业务参数：参与费用核算，不允许删除">核心</span>
                          : <button className="btn-sm btn-sm-danger" onClick={() => handleDelete(c)}>删除</button>}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          {list.length === 0 && <div className="empty"><Icon name="empty" size={40} chip /><div>暂无系统参数</div></div>}

          {/* 有未保存改动时才出现：不占常驻位置，也不至于改完忘了点保存 */}
          {dirtyKeys.length > 0 && (
            <div className="cfg-save-bar">
              <span className="cfg-save-tip">
                <Icon name="info" size={15} chip />
                已修改 <strong>{dirtyKeys.length}</strong> 项，尚未保存
              </span>
              <div className="btn-group">
                <button className="btn-sm btn-sm-default" onClick={() => setDrafts({})} disabled={savingCfg}>
                  撤销全部
                </button>
                <button className="btn-sm btn-sm-primary" onClick={saveDrafts} disabled={savingCfg}>
                  <Icon name="save" size={13} />{savingCfg ? '保存中…' : '保存全部'}
                </button>
              </div>
            </div>
          )}
        </>
      )}

      {tab === 'log' && (
        <>
          {/* 需求 4.2.1：日志支持按操作人/内容、时间、模块、结果筛选；条件即时生效。
              布局与订单/结算页统一（.filter-bar 搜索框 + 下拉 + 日期组 + 图标重置），
              不再用「标签在上、输入在下」的高栏，筛选区更紧凑。 */}
          <div className="filter-bar">
            <div className="filter-inputs log-filter-inputs">
              <Icon name="search" size={14} className="filter-icon" />
              <input
                className="filter-kw"
                placeholder="搜索操作人 / 操作内容…"
                value={logFilter.keyword}
                onChange={e => patchLogFilter({ keyword: e.target.value })}
              />
              <select value={logFilter.module}
                onChange={e => patchLogFilter({ module: e.target.value })}>
                <option value="">全部模块</option>
                {logModules.map(m => <option key={m} value={m}>{m}</option>)}
              </select>
              <select value={logFilter.result}
                onChange={e => patchLogFilter({ result: e.target.value })}>
                <option value="">全部结果</option>
                <option value="成功">成功</option>
                <option value="失败">失败</option>
              </select>
              <span className="filter-sep" />
              <span className="filter-date-group">
                <span className="filter-tilde">从</span>
                <input type="date" className="filter-date" value={logFilter.from}
                  max={logFilter.to || undefined}
                  onChange={e => patchLogFilter({ from: e.target.value })}
                  aria-label="开始日期" />
              </span>
              <span className="filter-date-group">
                <span className="filter-tilde">至</span>
                <input type="date" className="filter-date" value={logFilter.to}
                  min={logFilter.from || undefined}
                  onChange={e => patchLogFilter({ to: e.target.value })}
                  aria-label="结束日期" />
              </span>
              {hasLogFilter && (
                <button className="btn-sm btn-sm-default" onClick={resetLogFilter}
                  title={`重置为近 ${DEFAULT_LOG_DAYS} 天`} aria-label={`重置为近 ${DEFAULT_LOG_DAYS} 天`}>
                  <Icon name="refresh" size={14} />
                </button>
              )}
            </div>
            <span className="filter-count">
              {loading ? '统计中…' : `筛选出 ${logTotal} 条`}
              {logRefreshedAt && !loading && (
                <span className="filter-refreshed"> · 更新于 {logRefreshedAt.toLocaleTimeString('zh-CN', { hour12: false })}</span>
              )}
            </span>
          </div>

          {/* 备份文件列表从这里移到右上角「备份文件」弹窗：夹在筛选条和日志表之间，
              既打断日志阅读，又容易被当成日志的一部分 */}

          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>日志ID</th><th>操作人</th><th>操作模块</th><th>操作内容</th>
                  <th>结果</th><th>IP 地址</th><th>操作时间</th>
                </tr>
              </thead>
              <tbody>
                {logs.map(l => (
                  <tr key={l.logId}>
                    <td className="mono">{l.logId}</td>
                    {/* 只给 ID 等于没给：姓名 + 账号 + 身份，账号被删时回退到 #ID */}
                    <td>
                      {l.operatorName || l.operatorAccount ? (
                        <span className="op-cell">
                          <strong>{l.operatorName || l.operatorAccount}</strong>
                          <span className="op-sub">
                            {l.operatorAccount && l.operatorName ? l.operatorAccount : ''}
                            {l.operatorType === 'customer' ? '顾客' : l.operatorType === 'staff' ? '工作人员' : ''}
                          </span>
                        </span>
                      ) : (
                        <span className="op-cell">
                          <strong>{l.userId != null ? `#${l.userId}` : '系统'}</strong>
                          {l.operatorType === 'unknown' && <span className="op-sub">账号已删除</span>}
                        </span>
                      )}
                    </td>
                    <td>{l.operateModule || '—'}</td>
                    <td>{l.operateContent || '—'}</td>
                    <td>
                      <span className={`status-tag ${l.operateResult === '成功' ? 'status-success' : 'status-danger'}`}>
                        {l.operateResult || '—'}
                      </span>
                    </td>
                    <td className="mono">{l.ipAddress || '—'}</td>
                    <td className="cell-time">{fmtDT(l.operateTime)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
          {!loading && logs.length === 0 && (
            <div className="empty"><Icon name="empty" size={40} chip /><div>没有符合条件的操作日志</div></div>
          )}
          <Pagination
            total={logTotal} page={page} pageSize={logPageSize}
            onChange={setPage}
            onPageSizeChange={n => { setLogPageSize(n); setPage(1) }}
          />
          <div className="log-immutable-tip">操作日志不可删除、不可篡改，实现全流程可追溯</div>
        </>
      )}

      {/* 备份文件（需求 4.2.1 / 6.4）：加密落盘、可下载留档、可直接回滚。
          放在弹窗里而不是页面里——它属于「运维动作」，不该占着操作日志的主视线 */}
      {backupOpen && (
        <div className="modal-overlay" onClick={() => setBackupOpen(false)}>
          <div className="modal modal-lg" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>备份文件（AES-256-GCM 加密）</span>
              <button className="close-btn" onClick={() => setBackupOpen(false)}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="callout callout-info" style={{ marginBottom: 12 }}>
                <Icon name="info" size={18} chip />
                <span>每日 02:30 自动备份；也可以点「数据库备份」立即生成一份。下载后请妥善保管，恢复操作会覆盖当前全部业务数据。</span>
              </div>
              <div className="table-wrap">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th style={{ width: '44%' }}>备份文件</th>
                      <th>大小</th>
                      <th>生成时间</th>
                      <th style={{ width: 150 }}>操作</th>
                    </tr>
                  </thead>
                  <tbody>
                    {backups.length === 0 ? (
                      <tr><td colSpan={4} style={{ textAlign: 'center', color: 'var(--text-3)' }}>暂无备份文件，点右上角「数据库备份」可立即生成</td></tr>
                    ) : backups.map((b: any) => (
                      <tr key={b.fileName}>
                        <td className="mono">{b.fileName}</td>
                        <td className="num">{(b.sizeBytes / 1024).toFixed(1)} KB</td>
                        <td>{new Date(b.createdAt).toLocaleString()}</td>
                        <td>
                          <button className="btn-sm" onClick={() => handleDownloadBackup(b.fileName)}>
                            <Icon name="download" size={14} />下载
                          </button>
                          <button
                            className="btn-sm btn-sm-danger" style={{ marginLeft: 6 }}
                            disabled={!!restoring}
                            onClick={() => handleRestore(b.fileName)}
                          >
                            <Icon name={restoring === b.fileName ? 'refresh' : 'refund'} size={14} />
                            {restoring === b.fileName ? '恢复中…' : '恢复'}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={() => { loadBackups(); toast('备份列表已刷新', 'success') }}>
                <Icon name="refresh" size={14} />刷新列表
              </button>
              <button className="btn-sm btn-sm-primary" onClick={() => setBackupOpen(false)}>关闭</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
