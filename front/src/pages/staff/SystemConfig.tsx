import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useUI } from '@/components/UIProvider'
import { configApi, logApi } from '@/api'
import Icon from '@/components/Icon'
import PayConfigPanel from './components/PayConfigPanel'
import { fmtDT, shiftDateInput, todayInput } from '@/utils/datetime'

/** 参数说明补充：用于把 config_key 翻译成更易懂的中文名 */
const CONFIG_LABEL: Record<string, string> = {
  daily_rent: '日租金',
  deposit: '默认押金',
  overdue_multiplier: '超时租金倍数',
  max_login_fail: '最大登录失败次数',
  lock_minutes: '账号锁定时长（分钟）',
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

const makeLogFilter = () => ({
  userId: '',
  from: shiftDateInput(todayInput(), -(DEFAULT_LOG_DAYS - 1)),
  to: todayInput(),
  module: '',
  result: '',
  keyword: '',
})

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
  const [list, setList] = useState<any[]>([])
  const [logs, setLogs] = useState<any[]>([])
  const [page, setPage] = useState(1)
  // 日志筛选（需求 4.2.1）：draft 是正在输入的，applied 是已生效的（点「查询」才发请求）
  const [logTotal, setLogTotal] = useState(0)
  const [logModules, setLogModules] = useState<string[]>([])
  const [logDraft, setLogDraft] = useState(makeLogFilter)
  const [logFilter, setLogFilter] = useState(makeLogFilter)
  const [editKey, setEditKey] = useState<string | null>(null)
  const [editValue, setEditValue] = useState('')
  const [loading, setLoading] = useState(true)
  const [backingUp, setBackingUp] = useState(false)
  const [resetting, setResetting] = useState(false)
  // 备份文件列表：加密 .erbk，可下载/回滚。恢复是高危操作，按文件名逐个确认
  const [backups, setBackups] = useState<any[]>([])
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

  const loadLogs = async (p = page) => {
    setLoading(true)
    try {
      const res: any = await logApi.list({
        page: p,
        size: 20,
        userId: logFilter.userId ? Number(logFilter.userId) : undefined,
        from: logFilter.from || undefined,
        to: logFilter.to || undefined,
        module: logFilter.module || undefined,
        result: logFilter.result || undefined,
        keyword: logFilter.keyword || undefined,
      })
      setLogs(res.data?.items || [])
      setLogTotal(res.data?.total ?? 0)
    } catch (e: any) { toast(e?.message || '日志加载失败', 'error') }
    finally { setLoading(false) }
  }

  // 模块下拉选项取日志里实际出现过的值，避免前端硬编码模块名与后端漂移
  const loadLogModules = () => {
    logApi.modules().then((r: any) => setLogModules(r.data || [])).catch(() => setLogModules([]))
  }

  const applyLogFilter = () => {
    setLogFilter({ ...logDraft })
    setPage(1)
  }

  /** 重置 = 回到默认条件（日期回到近 30 天），而不是清成空区间 */
  const resetLogFilter = () => {
    const next = makeLogFilter()
    setLogDraft(next)
    setLogFilter(next)
    setPage(1)
  }

  useEffect(() => { loadConfig(); loadComfy() }, [])
  useEffect(() => { if (tab === 'log') { loadLogs(page); loadLogModules(); loadBackups() } }, [tab, page, logFilter])

  const logPages = Math.max(1, Math.ceil(logTotal / 20))

  const handleSave = async (c: any) => {
    const value = editValue.trim()
    if (!value) { toast('参数值不能为空', 'warning'); return }
    if (Number.isNaN(Number(value)) || Number(value) < 0) { toast('该参数需为不小于 0 的数字', 'warning'); return }
    const ok = await confirm({
      title: '修改系统参数',
      message: `确定将「${CONFIG_LABEL[c.configKey] || c.configKey}」由 ${c.configValue} 修改为 ${value} 吗？该参数将影响后续订单的费用核算。`,
      type: 'question',
      confirmText: '确认修改',
    })
    if (!ok) return
    try {
      await configApi.update(c.configKey, value)
      toast('参数已更新', 'success')
      setEditKey(null)
      loadConfig()
    } catch (e: any) { toast(e?.message || '更新失败', 'error') }
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
      toast(`备份成功：${d.fileName}（${d.sizeKB}KB，共 ${d.tableCount} 张表，已加密）`, 'success')
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
    <div className="card">
      <div className="card-title">
        <Icon name="config" size={18} chip />系统配置
        {tab === 'log' && (
          <div className="toolbar-actions" style={{ marginLeft: 'auto' }}>
            <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={handleBackup} disabled={backingUp || resetting}>
              <Icon name={backingUp ? 'refresh' : 'save'} size={14} /><span className="btn-label">{backingUp ? '备份中…' : '数据库备份'}</span>
            </button>
            <button className="btn-sm btn-sm-danger btn-icon-sm" onClick={handleResetDb} disabled={backingUp || resetting}>
              <Icon name={resetting ? 'refresh' : 'empty'} size={14} /><span className="btn-label">{resetting ? '初始化中…' : '数据库初始化'}</span>
            </button>
            <button className="btn-sm btn-sm-default btn-icon-sm" onClick={() => loadLogs(page)}>
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

      {tab === 'pay' && <PayConfigPanel />}

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
                {list.map(c => (
                  <tr key={c.configId}>
                    <td className="mono">{c.configKey}</td>
                    <td>{CONFIG_LABEL[c.configKey] || '—'}</td>
                    <td className="num">
                      {editKey === c.configKey
                        ? <input
                          className="inline-input"
                          value={editValue}
                          onChange={e => setEditValue(e.target.value)}
                          onKeyDown={e => { if (e.key === 'Enter') handleSave(c); if (e.key === 'Escape') setEditKey(null) }}
                          autoFocus
                        />
                        : <strong>{c.configValue}</strong>}
                    </td>
                    <td>{c.configDesc}</td>
                    <td className="cell-time">{fmtDT(c.updateTime)}</td>
                    <td>
                      {editKey === c.configKey
                        ? <div className="btn-group">
                          <button className="btn-sm btn-sm-primary" onClick={() => handleSave(c)}>保存</button>
                          <button className="btn-sm btn-sm-default" onClick={() => setEditKey(null)}>取消</button>
                        </div>
                        : <div className="btn-group">
                          <button className="btn-sm btn-sm-default" onClick={() => { setEditKey(c.configKey); setEditValue(String(c.configValue)) }}>修改</button>
                          {!CORE_KEYS.includes(c.configKey) && (
                            <button className="btn-sm btn-sm-danger" onClick={() => handleDelete(c)}>删除</button>
                          )}
                        </div>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {list.length === 0 && <div className="empty"><Icon name="empty" size={40} chip /><div>暂无系统参数</div></div>}
        </>
      )}

      {tab === 'log' && (
        <>
          {/* 需求 4.2.1：日志支持按用户、时间、操作类型筛选查询 */}
          <div className="filter-row">
            <label className="field-inline">
              <span>操作人ID</span>
              <input
                type="number" min="1" style={{ width: 100 }}
                value={logDraft.userId}
                placeholder="用户ID"
                onChange={e => setLogDraft({ ...logDraft, userId: e.target.value })}
                onKeyDown={e => { if (e.key === 'Enter') applyLogFilter() }}
              />
            </label>
            <label className="field-inline">
              <span>开始日期</span>
              <input type="date" value={logDraft.from}
                onChange={e => setLogDraft({ ...logDraft, from: e.target.value })} />
            </label>
            <label className="field-inline">
              <span>结束日期</span>
              <input type="date" value={logDraft.to}
                min={logDraft.from || undefined}
                onChange={e => setLogDraft({ ...logDraft, to: e.target.value })} />
            </label>
            <label className="field-inline">
              <span>操作模块</span>
              <select value={logDraft.module}
                onChange={e => setLogDraft({ ...logDraft, module: e.target.value })}>
                <option value="">全部</option>
                {logModules.map(m => <option key={m} value={m}>{m}</option>)}
              </select>
            </label>
            <label className="field-inline">
              <span>操作结果</span>
              <select value={logDraft.result}
                onChange={e => setLogDraft({ ...logDraft, result: e.target.value })}>
                <option value="">全部</option>
                <option value="成功">成功</option>
                <option value="失败">失败</option>
              </select>
            </label>
            <label className="field-inline">
              <span>内容关键字</span>
              <input
                value={logDraft.keyword}
                placeholder="操作内容包含…"
                onChange={e => setLogDraft({ ...logDraft, keyword: e.target.value })}
                onKeyDown={e => { if (e.key === 'Enter') applyLogFilter() }}
              />
            </label>
            <button className="btn-sm btn-sm-primary" onClick={applyLogFilter}>
              <Icon name="search" size={14} />查询
            </button>
            <button className="btn-sm" onClick={resetLogFilter}>重置为近 {DEFAULT_LOG_DAYS} 天</button>
            <span className="filter-hint">默认显示近 {DEFAULT_LOG_DAYS} 天；清空两个日期可查全部历史</span>
          </div>

          {/* 备份与恢复（需求 4.2.1 / 6.4）：文件加密存储，可下载留档，也可直接回滚 */}
          <div className="table-wrap" style={{ marginBottom: 16 }}>
            <table className="data-table">
              <thead>
                <tr>
                  <th style={{ width: '42%' }}>备份文件</th>
                  <th>大小</th>
                  <th>生成时间</th>
                  <th style={{ width: 150 }}>操作</th>
                </tr>
              </thead>
              <tbody>
                {backups.length === 0 ? (
                  <tr><td colSpan={4} style={{ textAlign: 'center', color: 'var(--text-3)' }}>暂无备份文件（每日 02:30 自动备份，也可点右上角「数据库备份」立即生成）</td></tr>
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

          <div className="table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>日志ID</th><th>操作人ID</th><th>操作模块</th><th>操作内容</th>
                  <th>结果</th><th>IP 地址</th><th>操作时间</th>
                </tr>
              </thead>
              <tbody>
                {logs.map(l => (
                  <tr key={l.logId}>
                    <td className="mono">{l.logId}</td>
                    <td>{l.userId ?? '—'}</td>
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
          <div className="pager">
            <button className="btn-sm btn-sm-default" disabled={page <= 1} onClick={() => setPage(p => p - 1)}>上一页</button>
            <span>第 {page} / {logPages} 页 · 共 {logTotal} 条</span>
            <button className="btn-sm btn-sm-default" disabled={page >= logPages} onClick={() => setPage(p => p + 1)}>下一页</button>
            <span className="pager-tip">操作日志不可删除、不可篡改，实现全流程可追溯</span>
          </div>
        </>
      )}
    </div>
  )
}
