import { useEffect, useRef, useState } from 'react'
import { useUI } from '@/components/UIProvider'
import { useSearchParams } from 'react-router-dom'
import { vehicleApi, configApi, checkApi } from '@/api'
import type { Vehicle } from '@/types'
import Icon from '@/components/Icon'
import DictThFilter from '@/components/DictThFilter'
import ImageLightbox from '@/components/ImageLightbox'
import OverflowMenu from '@/components/OverflowMenu'
import RowActions from '@/components/RowActions'
import Pagination from '@/components/Pagination'
import SortTh, { type SortDir } from '@/components/SortTh'
import VehicleQrModal from './components/VehicleQrModal'
import VehicleQrLookup from './components/VehicleQrLookup'
import VehicleCheckPanel from './components/VehicleCheckPanel'
import MaintenancePanel from './components/MaintenancePanel'
import AiImagePanel from './components/AiImagePanel'
import BatchCreateModal from './components/BatchCreateModal'
import BatchEditModal from './components/BatchEditModal'
import { useAuth, ROLE } from '@/hooks/useAuth'
import { todayInput, toDateInputFrom } from '@/utils/datetime'

const statusMap: Record<string, string> = {
  '空闲': 'status-success',
  '在租': 'status-primary',
  '维保': 'status-warning',
  '报废': 'status-danger',
}
/** 展示与统计用的全部状态 */
const STATUSES = ['空闲', '在租', '维保', '报废']
/** 车辆档案列表默认每页行数；用户可在分页条上改（盘点时想一屏看全） */
const DEFAULT_PAGE_SIZE = 10
/**
 * 建档/编辑下拉里**可选**的状态：不含「在租」。
 * 在租是开单时自动占用车辆产生的，人工选出来会造出一辆「没有订单却租不出去」的车，
 * 既租不掉也没人能结算（后端同口径拦截，前端这里先把入口收掉）。
 */
const SELECTABLE_STATUSES = ['空闲', '维保', '报废']
/** 新增车辆表单初值：采购时间直接带出今天，用户可改成实际采购日期 */
const emptyVehicle = () => ({
  vehicleNo: '', brand: '', frameNumber: '',
  buyTime: todayInput(), buyPrice: '', vehicleStatus: '空闲',
  // 车辆独立计价：留空 = 用系统默认价（后端回退），填了才按本车价收费
  dailyRent: '', deposit: ''
})

// ── 车辆图片约束（与后端 ImageStorage 口径一致，前端先拦一道给即时反馈） ──
const IMG_EXTS = ['jpg', 'jpeg', 'png', 'webp', 'gif']
const MAX_IMG_MB = 5
/** 多图预留上限：当前档案字段只存一张主图，其余图随建档一并落盘，为多图归档铺路 */
const MAX_IMAGES = 5

/** 校验图片格式与大小，返回错误文案（空串 = 通过）。扩展名不信任 accept，按真实文件名判。 */
const validateFiles = (files: File[]): string => {
  for (const f of files) {
    const ext = f.name.split('.').pop()?.toLowerCase() || ''
    if (!IMG_EXTS.includes(ext)) return `不支持的图片格式：${f.name}（仅限 jpg / png / webp / gif）`
    if (f.size > MAX_IMG_MB * 1024 * 1024) return `「${f.name}」超过 ${MAX_IMG_MB}MB，请压缩后再选择`
  }
  return ''
}

/** 新增流程里待落库的图片：file = 本地待上传 */
type PendingImage = { key: string; preview: string; file: File }
const newKey = () => `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`

/** 车辆图片：无图时显示占位图标，避免列表里出现破图；有图可点击放大 */
const thumb = (v: Vehicle, onZoom: (src: string) => void) => v.imageUrl
  ? <img className="vehicle-thumb is-clickable" src={v.imageUrl} alt={v.vehicleNo}
      title="点击放大" onClick={() => onZoom(v.imageUrl!)} />
  : <span className="vehicle-thumb vehicle-thumb-empty"><Icon name="vehicles" size={16} /></span>

/** 车辆未单独定价时，列表里提示「用默认价」，避免管理员误以为漏配 */
const isCustomPriced = (v: Vehicle) => v.dailyRent != null || v.deposit != null

/**
 * 车辆管理页。
 *
 * 结构图「车辆管理模块」下含「车辆档案管理 / 车辆状态监控 / 维保与报废管理」三个子项，
 * 因此原独立页 维保管理（/maintenance）并入本页作为第三个页签，与结构图对齐。
 * 旧链接 /maintenance 仍可用（路由重定向到 /vehicles?view=maintenance）。
 *
 * 角色差异：管理员可见全部三个页签；前台（用况4 允许其上报故障）只可见「维保管理」页签，
 * 且其中的完成与报废动作仍仅管理员可操作（面板内部按角色再判一次）。
 */
type ViewKey = 'list' | 'check' | 'maintenance'

export default function Vehicles() {
  const { toast, confirm, prompt } = useUI()
  const { hasRole } = useAuth()
  const isAdmin = hasRole([ROLE.ADMIN])
  const [searchParams] = useSearchParams()
  const [list, setList] = useState<Vehicle[]>([])
  const [keyword, setKeyword] = useState(searchParams.get('q') || '')
  const [status, setStatus] = useState('')
  const [loading, setLoading] = useState(true)
  const [showModal, setShowModal] = useState(false)
  const [editId, setEditId] = useState<number | null>(null)
  /** 编辑态车辆的**原始状态**：保存时若与表单里的状态不一致，说明本次改动含状态变更，
   *  需二次输入管理员密码（用况4 扩展用况3）。新增态为空串，不参与判断。 */
  const [editStatus0, setEditStatus0] = useState('')
  const [form, setForm] = useState(emptyVehicle())
  const [errors, setErrors] = useState<Record<string, string>>({})
  /** 当前查看二维码的车辆（null = 关闭） */
  const [qrVehicle, setQrVehicle] = useState<Vehicle | null>(null)
  /** 扫码查询弹窗开关 */
  const [showLookup, setShowLookup] = useState(false)
  /** 批量操作：勾选的车辆主键集合（仅在车辆档案页签、管理员可见） */
  const [selected, setSelected] = useState<number[]>([])
  /** 批量新增 / 批量编辑弹窗开关 */
  const [showBatchCreate, setShowBatchCreate] = useState(false)
  const [showBatchEdit, setShowBatchEdit] = useState(false)
  /** 编辑态已落库的车辆图片（新增车辆走 pendingImages，建档后一并落库） */
  const [imageUrl, setImageUrl] = useState<string>('')
  const [uploading, setUploading] = useState(false)
  /** 新增流程：本地选择/生成好、等「保存」后一起落库的图片 */
  const [pendingImages, setPendingImages] = useState<PendingImage[]>([])
  const [imgError, setImgError] = useState('')
  /** 放大预览的图片地址（null = 关闭）；列表缩略图与弹窗预览共用 */
  const [lightbox, setLightbox] = useState<string | null>(null)
  /** 隐藏的 file input：选择与「替换某个槽位」共用，replaceKey 记录替换目标 */
  const fileInputRef = useRef<HTMLInputElement | null>(null)
  const replaceKey = useRef<string | null>(null)

  const [brandOptions, setBrandOptions] = useState<string[]>([])
  const [brandFilter, setBrandFilter] = useState('')
  /** 档案列表当前页码：筛选条件变化时归零到第 1 页 */
  const [page, setPage] = useState(1)
  /** 服务端分页返回的总条数与各状态角标 */
  const [total, setTotal] = useState(0)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  /** 表头排序：空串 = 车牌升序（台账自然顺序） */
  const [sortBy, setSortBy] = useState('')
  const [sortDir, setSortDir] = useState<SortDir>('desc')
  const [counts, setCounts] = useState<Record<string, number>>({})
  /**
   * 页面级页签：list=车辆档案，check=车辆盘点，maintenance=维保管理（后两者原为独立页并入）。
   * 支持 ?view= 直达（供旧路由重定向与通知链接使用）；前台无车辆档案权限，默认落在维保页签。
   */
  const [view, setView] = useState<ViewKey>(() => {
    const v = searchParams.get('view')
    if (v === 'maintenance') return 'maintenance'
    if (v === 'check' && hasRole([ROLE.ADMIN])) return 'check'
    return hasRole([ROLE.ADMIN]) ? 'list' : 'maintenance'
  })

  const load = async () => {
    setLoading(true)
    try {
      // 服务端分页：品牌筛选也下推给后端，否则只对当前页筛会造成总数与页码失真
      const res: any = await vehicleApi.page({
        status: status || undefined,
        keyword: keyword || undefined,
        brand: brandFilter || undefined,
        sortBy: sortBy || undefined,
        sortDesc: sortDir === 'desc',
        page,
        pageSize,
      })
      setList(res.data?.items || [])
      setTotal(res.data?.total || 0)
    } catch (e: any) {
      toast(e?.message || '车辆列表加载失败', 'error')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { load() }, [status, keyword, brandFilter, sortBy, sortDir, page, pageSize])

  // 状态角标单独统计：分页后前端拿不到全集，角标不能靠当前页算
  useEffect(() => {
    (async () => {
      try {
        const res: any = await vehicleApi.stats({ keyword: keyword || undefined, brand: brandFilter || undefined })
        setCounts(res.data || {})
      } catch { /* 统计失败时角标退化为 0，不影响列表 */ }
    })()
  }, [keyword, brandFilter, status])

  // 品牌筛选选项来自数据字典（dict.brand），字典更新后筛选选项随之变化
  useEffect(() => {
    (async () => {
      try {
        const res: any = await configApi.dictList()
        const g = (res.data || []).find((x: any) => x.group === 'brand')
        setBrandOptions((g?.items || []).map((i: any) => i.value as string))
      } catch { /* 字典不可用时筛选退化为「全部」，不影响列表 */ }
    })()
  }, [])

  const validate = () => {
    const e: Record<string, string> = {}
    if (!form.vehicleNo.trim()) e.vehicleNo = '请输入车牌号/车辆编号'
    if (!form.brand.trim()) e.brand = '请输入车辆品牌'
    if (form.buyPrice && Number(form.buyPrice) < 0) e.buyPrice = '购入价不能为负数'
    // 车辆独立计价的两项：留空合法（= 用系统默认价），填了就必须是非负数字
    if (form.dailyRent !== '' && Number(form.dailyRent) < 0) e.dailyRent = '日租金不能为负数'
    if (form.deposit !== '' && Number(form.deposit) < 0) e.deposit = '押金不能为负数'
    setErrors(e)
    return Object.keys(e).length === 0
  }

  /** 释放本地预览的 objectURL（仅 file 项有），防止内存泄漏 */
  const disposePending = (items: PendingImage[]) =>
    items.forEach(p => { if (p.file) URL.revokeObjectURL(p.preview) })

  /** 关闭弹窗并复位表单/图片状态 */
  const closeAndReset = () => {
    setShowModal(false)
    disposePending(pendingImages)
    setPendingImages([])
    setImgError('')
  }

  /**
   * 管理员关键操作二次校验（用况4 扩展用况3「管理员操作车辆状态，使其变成其他状态
   * 需要管理员密码」）。弹密码框，返回输入值；取消返回 null。
   * 明文密码只随本次请求发出，不落任何本地存储；真正的校验在后端
   * （AuthService.VerifyStaffPassword，BCrypt 比对当前登录管理员本人）。
   */
  const askAdminPassword = (message: string) => prompt({
    title: '管理员密码校验',
    message,
    inputType: 'password',
    placeholder: '请输入当前管理员登录密码',
    confirmText: '确认变更',
    validate: v => (v.trim() ? null : '请输入登录密码'),
  })

  const handleSave = async () => {
    if (!validate()) return
    let attachFail: string | null = null
    try {
      const payload = {
        ...form,
        vehicleNo: form.vehicleNo.trim(),
        brand: form.brand.trim(),
        // 采购时间：空则传 null，由后端保留原值（历史车辆无采购日期时不强求补录）
        buyTime: form.buyTime || null,
        buyPrice: form.buyPrice === '' ? 0 : Number(form.buyPrice),
        // 车辆独立计价：留空必须传 null（不是 0），后端据此判定「回退系统配置」。
        // 传 0 会被理解成「本车日租金 0 元」，下单金额直接算成 0。
        dailyRent: form.dailyRent === '' ? null : Number(form.dailyRent),
        deposit: form.deposit === '' ? null : Number(form.deposit),
      }
      if (editId) {
        // 档案编辑中若一并改动了「车辆状态」，按「状态变更需管理员密码」二次校验；
        // 仅改品牌/租金等其它字段不打扰。用户取消密码框则整体放弃保存。
        let opPwd: string | undefined
        if (form.vehicleStatus !== editStatus0) {
          const pwd = await askAdminPassword(
            `本次保存将把车辆「${form.vehicleNo}」的状态由「${editStatus0}」改为「${form.vehicleStatus}」，需管理员密码确认。`)
          if (pwd == null) return
          opPwd = pwd
        }
        await vehicleApi.update(editId, payload, opPwd)
        toast('车辆信息已更新', 'success')
      } else {
        const created: any = await vehicleApi.create(payload)
        const newId = created?.data?.vehicleId
        if (newId && pendingImages.length) {
          // **倒序落库**：每次上传都会覆盖 image_url，把主图（第 0 张）留在最后写，
          // 才能保证档案最终展示主图；其余图片文件也已存到服务器，为多图归档预留。
          for (const p of [...pendingImages].reverse()) {
            try {
              await vehicleApi.uploadImage(newId, p.file)
            } catch (e: any) { attachFail = e?.message || '图片保存失败' }
          }
        }
        toast(attachFail ? `车辆已入库，但图片保存失败：${attachFail}，可在编辑弹窗重试` : '车辆已入库',
          attachFail ? 'warning' : 'success')
      }
      closeAndReset()
      load()
    } catch (e: any) { toast(e?.message || '操作失败', 'error') }
  }

  /** 编辑态：选图即上传（单张覆盖），不走「先暂存后提交」——改的是既有档案，立即可见 */
  const handleUpload = async (file?: File | null) => {
    if (!file || !editId) return
    const err = validateFiles([file])
    if (err) { setImgError(err); return }
    setImgError('')
    setUploading(true)
    try {
      const res: any = await vehicleApi.uploadImage(editId, file)
      setImageUrl(res?.data?.url || '')
      toast('车辆图片已上传', 'success')
      load()
    } catch (e: any) {
      toast(e?.message || '图片上传失败', 'error')
    } finally {
      setUploading(false)
    }
  }

  const handleRemoveImage = async () => {
    if (!editId) return
    try {
      await vehicleApi.removeImage(editId)
      setImageUrl('')
      toast('车辆图片已清除', 'success')
      load()
    } catch (e: any) { toast(e?.message || '清除失败', 'error') }
  }

  // ── 图片选择器：新增态本地暂存（多张），编辑态立即上传（单张） ──
  const addFiles = (files: File[]) => {
    const err = validateFiles(files)
    if (err) { setImgError(err); return }
    setImgError('')
    setPendingImages(prev => {
      const room = Math.max(MAX_IMAGES - prev.length, 0)
      if (files.length > room) toast(`最多选择 ${MAX_IMAGES} 张，已保留前 ${room} 张`, 'warning')
      const added = files.slice(0, room)
        .map(f => ({ key: newKey(), preview: URL.createObjectURL(f), file: f }))
      return [...prev, ...added]
    })
  }

  /** 用新文件替换某个槽位（保留位置；被替换的是主图时新图自动成为主图） */
  const replaceFile = (key: string, file: File) => {
    const err = validateFiles([file])
    if (err) { setImgError(err); return }
    setImgError('')
    setPendingImages(prev => prev.map(p => {
      if (p.key !== key) return p
      if (p.file) URL.revokeObjectURL(p.preview)
      return { key: p.key, preview: URL.createObjectURL(file), file }
    }))
  }

  const removePending = (key: string) =>
    setPendingImages(prev => {
      const item = prev.find(p => p.key === key)
      if (item?.file) URL.revokeObjectURL(item.preview)
      return prev.filter(p => p.key !== key)
    })

  /**
   * AI 生成结果落地。
   * 编辑态：AiImagePanel 已调 setImageUrl 写库，这里同步本地 state 并刷新列表；
   * 新增态：成品拉成的 File 加入 pendingImages，与「选择图片」同一通道，保存建档时落库。
   */
  const handleAiUse = (url: string, file?: File) => {
    if (editId) { setImageUrl(url); load(); return }
    if (!file) return
    if (pendingImages.length >= MAX_IMAGES) { toast(`最多 ${MAX_IMAGES} 张图片`, 'warning'); return }
    setPendingImages(prev => [...prev, { key: newKey(), preview: URL.createObjectURL(file), file }])
  }

  /** 共用入口：普通选图与手机拍照拍到的文件都汇到这里（编辑态单张即传，新增态暂存） */
  const ingestFiles = (files: File[]) => {
    if (files.length === 0) return
    if (editId) { handleUpload(files[0]); return }
    const target = replaceKey.current
    replaceKey.current = null
    if (target) replaceFile(target, files[0])
    else addFiles(files)
  }

  const onFileInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(e.target.files || [])
    e.target.value = ''
    ingestFiles(files)
  }

  const handleEdit = (v: Vehicle) => {
    setEditId(v.vehicleId)
    setEditStatus0(v.vehicleStatus)   // 记下原始状态，保存时据此判断是否需要密码校验
    setImageUrl(v.imageUrl || '')
    setForm({
      vehicleNo: v.vehicleNo,
      brand: v.brand || '',
      frameNumber: v.frameNumber || '',
      // 编辑老档案时采购时间可能为空，这里补今天：与「时间输入一律默认当前值」的约定一致，
      // 用户可再改，不必手工点日历。
      buyTime: toDateInputFrom(v.buyTime) || todayInput(),
      buyPrice: v.buyPrice != null ? String(v.buyPrice) : '',
      vehicleStatus: v.vehicleStatus,
      dailyRent: v.dailyRent != null ? String(v.dailyRent) : '',
      deposit: v.deposit != null ? String(v.deposit) : '',
    })
    setErrors({})
    setImgError('')
    setShowModal(true)
  }

  const handleDelete = async (v: Vehicle) => {
    const ok = await confirm({
      title: '删除车辆',
      message: `确定要删除车辆 ${v.vehicleNo} 吗？车辆档案与租赁记录将一并清理，该操作不可恢复。`,
      type: 'warning',
      danger: true,
      confirmText: '确认删除',
    })
    if (!ok) return
    try {
      await vehicleApi.remove(v.vehicleId)
      toast('车辆已删除', 'success')
      load()
    } catch (e: any) { toast(e?.message || '删除失败', 'error') }
  }

  /** 更改车辆状态（用况4 步骤4）：先弹管理员密码校验，通过后才提交，取消则状态不变 */
  const handleStatus = async (v: Vehicle, next: string) => {
    if (next === v.vehicleStatus) return
    // 即时拦截：把「在租 / 已报废」的车再次置为「报废」既无业务意义（报废不可逆、在租须先结算归还），
    // 也不该让管理员先输完密码、提交后才被后端拦下——在弹密码框之前就拦住。
    if (next === '报废' && (v.vehicleStatus === '在租' || v.vehicleStatus === '报废')) {
      toast(`车辆 ${v.vehicleNo} 当前为「${v.vehicleStatus}」，不可报废`, 'warning')
      return
    }
    const pwd = await askAdminPassword(
      `将车辆「${v.vehicleNo}」的状态由「${v.vehicleStatus}」改为「${next}」，需管理员密码确认。`)
    if (pwd == null) return
    try {
      await vehicleApi.updateStatus(v.vehicleId, next, pwd)
      toast(`车辆 ${v.vehicleNo} 状态已更新为「${next}」`, 'success')
      load()
    } catch (e: any) {
      toast(e?.message || '状态更新失败', 'error')
      // 失败时刷新一次，把下拉复位回库里的真实状态（否则受控 select 会停在用户选的值上）
      load()
    }
  }

  const countsRows = STATUSES.map(s => ({ s, n: counts[s] || 0 }))

  // ── 批量勾选：仅车辆档案页签（list）、管理员可见 ──
  // 服务端分页后前端只有当前页数据，因此"全选"作用于**当前页**；
  // 已勾选项跨页保留，批量改价/批量状态仍可先逐页勾选再一次性提交。
  const paged = list
  const pageCount = Math.max(1, Math.ceil(total / pageSize))
  const visibleIds = list.map(v => v.vehicleId)
  const allSelected = visibleIds.length > 0 && visibleIds.every(id => selected.includes(id))
  const toggleSelect = (id: number) =>
    setSelected(prev => prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id])
  const toggleSelectAll = () =>
    setSelected(prev => allSelected
      ? prev.filter(id => !visibleIds.includes(id))
      : Array.from(new Set([...prev, ...visibleIds])))
  const clearSelection = () => setSelected([])

  /** 点表头：同列翻转方向，换列则默认从大到小（先看最贵/最近）；再点一次可回到车牌顺序之外的最优默认值 */
  const toggleSort = (key: string) => {
    if (key === sortBy) setSortDir(d => (d === 'asc' ? 'desc' : 'asc'))
    else { setSortBy(key); setSortDir('desc') }
  }

  // 筛选条件一变就回到第 1 页（页码越界时也夹回最后一页，应对删车导致总数变少）
  useEffect(() => { setPage(p => Math.min(p, pageCount)) }, [pageCount])
  useEffect(() => { setPage(1) }, [status, keyword, brandFilter, sortBy, sortDir, pageSize])

  /** 弹窗里展示的图片：编辑态 = 已落库那张；新增态 = 待保存列表（第一张为主图） */
  const shownImages = editId
    ? (imageUrl ? [{ key: 'server', preview: imageUrl, primary: true, pending: false }] : [])
    : pendingImages.map((p, i) => ({ key: p.key, preview: p.preview, primary: i === 0, pending: true }))

  /** 盘点报表：按钮在页面标题行（与「车辆盘点」同一行靠右）。
   *  生成成功后把 refreshToken +1，盘点面板重拉数据，报表页签立刻可见。 */
  const [checkRefresh, setCheckRefresh] = useState(0)
  const [checkReporting, setCheckReporting] = useState(false)
  const genCheckReport = async () => {
    const ok = await confirm({
      title: '生成盘点报表',
      message: '将基于今日全部盘点记录生成盘点报表，是否继续？',
      type: 'question',
      confirmText: '生成报表',
    })
    if (!ok) return
    setCheckReporting(true)
    try {
      const res: any = await checkApi.generateReport(new Date().toISOString())
      toast(`盘点报表已生成：${res.data?.reportNo || ''}`, 'success')
      setCheckRefresh(n => n + 1)
    } catch (e: any) {
      toast(e?.message || '生成失败', 'error')
    } finally {
      setCheckReporting(false)
    }
  }

  return (
    <div className="card">
      <div className="card-title veh-card-title">
        <Icon name="vehicles" size={18} chip />
        {view === 'check' ? '车辆盘点' : view === 'maintenance' ? '维保管理' : '车辆台账'}
        {/* 计数用服务端的 total（全量命中），不能用 list.length：
            分页后 list 只有当前页 10 条，卡片上写「共 10 台」而右侧状态角标合计 26 台，
            两个数并排打架。 */}
        {view === 'list' && <span className="card-title-count">共 {total} 台</span>}
        <div className="toolbar-actions" style={{ marginLeft: 'auto' }}>
          {/* 车辆档案页签按钮 */}
          {view === 'list' && (
            <>
              <button className="btn-sm btn-sm-default btn-icon-sm" onClick={load} disabled={loading}>
                <Icon name="refresh" size={15} /><span className="btn-label">刷新</span>
              </button>
              <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={() => {
                setEditId(null); setEditStatus0(''); setImageUrl(''); setForm(emptyVehicle()); setErrors({})
                setImgError('')
                setShowModal(true)
              }}>
                <Icon name="create-order" size={15} color="#fff" /><span className="btn-label">新增车辆</span>
              </button>
              <OverflowMenu
                items={[
                  { key: 'lookup', label: '扫码查询', icon: 'qrcode', onClick: () => setShowLookup(true) },
                  { key: 'batch-create', label: '批量新增', icon: 'create-order', onClick: () => setShowBatchCreate(true) },
                ]}
              />
            </>
          )}
          {/* 车辆盘点页签：动作与页面标题同一行、靠右；标题行变窄时退化为图标
              （见 .veh-card-title 的容器查询），不会把「车辆盘点」标题挤走。 */}
          {view === 'check' && (
            <button
              className="btn-sm btn-sm-primary btn-icon-sm"
              onClick={genCheckReport}
              disabled={checkReporting}
              title="生成盘点报表"
            >
              <Icon name="reports" size={15} color="#fff" />
              <span className="btn-label">{checkReporting ? '生成中…' : '生成报表'}</span>
            </button>
          )}
          {/* 维保管理页签按钮 */}
          {view === 'maintenance' && (
            <>
              <button className="btn-sm btn-sm-primary btn-icon-sm" onClick={() => window.dispatchEvent(new CustomEvent('maintenance-add-work'))}>
                <Icon name="create-order" size={15} color="#fff" /><span className="btn-label">新增工单</span>
              </button>
            </>
          )}
        </div>
      </div>

      <div className="toolbar">
        <div className="tab-switch">
          {/* 车辆档案、车辆盘点属用况4 管理员能力，前台不可见 */}
          {isAdmin && (
            <>
              <button className={`tab-btn ${view === 'list' ? 'active' : ''}`} onClick={() => setView('list')}>
                车辆档案
              </button>
              <button className={`tab-btn ${view === 'check' ? 'active' : ''}`} onClick={() => setView('check')}>
                车辆盘点
              </button>
            </>
          )}
          {/* 维保与报废管理：管理员可见全部动作，前台可进入上报故障 */}
          <button className={`tab-btn ${view === 'maintenance' ? 'active' : ''}`} onClick={() => setView('maintenance')}>
            维保管理
          </button>
        </div>
        {view === 'list' && (
          <span className="title-chips">
            {countsRows.map(c => (
              <span key={c.s} className={`status-tag ${statusMap[c.s]}`}>{c.s} {c.n}</span>
            ))}
          </span>
        )}
        {view === 'list' && (
        <>
        <div className="search-box">
          <input placeholder="搜索车牌号 / 品牌" value={keyword} onChange={e => setKeyword(e.target.value)} />
          <select value={status} onChange={e => setStatus(e.target.value)}>
            <option value="">全部状态</option>
            {STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
          </select>
          {(keyword || status) && (
            <button className="btn-sm btn-sm-default" title="重置筛选条件"
              onClick={() => { setKeyword(''); setStatus('') }}>
              <Icon name="refresh" size={14} /><span className="btn-label">重置</span>
            </button>
          )}
        </div>
        </>
        )}
      </div>

      {/* 盘点页签：原「车辆盘点」独立页整体并入，档案相关工具栏与表格仅在档案页签显示 */}
      {view === 'check' ? (
        <VehicleCheckPanel refreshToken={checkRefresh} />
      ) : view === 'maintenance' ? (
        <MaintenancePanel />
      ) : (
      <>
      {isAdmin && selected.length > 0 && (
        <div className="batch-bar">
          <span className="batch-bar-info">已选 <strong>{selected.length}</strong> 辆车辆</span>
          <div className="batch-bar-actions">
            <button className="btn-sm btn-sm-primary" onClick={() => setShowBatchEdit(true)}>
              <Icon name="vehicles" size={14} color="#fff" />批量编辑
            </button>
            <button className="btn-sm btn-sm-default" onClick={clearSelection}>
              <Icon name="close" size={13} />取消选择
            </button>
          </div>
        </div>
      )}
      <div className="table-wrap">
        <table className="data-table with-checkbox">
          <thead>
            <tr>
              {isAdmin && <th className="col-check"><input type="checkbox" checked={allSelected} onChange={toggleSelectAll} aria-label="全选" /></th>}
              <th>图片</th><th>车牌号</th>
              <th>
                <span className="th-with-filter">品牌</span>
                <DictThFilter options={brandOptions} value={brandFilter} onChange={setBrandFilter} />
              </th>
              <th className="col-hide-lg">车架号</th>
              <SortTh label="采购时间" active={sortBy === 'buyTime'} dir={sortDir}
                      onClick={() => toggleSort('buyTime')} className="col-hide-lg" />
              <SortTh label="购入价" active={sortBy === 'buyPrice'} dir={sortDir}
                      onClick={() => toggleSort('buyPrice')} className="num col-hide-md" />
              <SortTh label="日租金" active={sortBy === 'dailyRent'} dir={sortDir}
                      onClick={() => toggleSort('dailyRent')} className="num" />
              <SortTh label="押金" active={sortBy === 'deposit'} dir={sortDir}
                      onClick={() => toggleSort('deposit')} className="num" />
              <th>状态</th><th>操作</th>
            </tr>
          </thead>
          <tbody>
            {paged.map(v => (
              <tr key={v.vehicleId}>
                {isAdmin && <td className="col-check"><input type="checkbox" checked={selected.includes(v.vehicleId)} onChange={() => toggleSelect(v.vehicleId)} aria-label="选择" /></td>}
                <td>{thumb(v, setLightbox)}</td>
                <td className="strong">{v.vehicleNo}</td>
                <td>{v.brand || '—'}</td>
                <td className="mono col-hide-lg">{v.frameNumber || '—'}</td>
                <td className="cell-time col-hide-lg">{v.buyTime ? String(v.buyTime).slice(0, 10) : '—'}</td>
                <td className="num col-hide-md">¥{v.buyPrice ?? '—'}</td>
                {/* 每辆车可以有自己的租金与押金；两项都为空表示走系统默认价 */}
                <td className="num">
                  {v.dailyRent != null
                    ? `¥${v.dailyRent}/天`
                    : <span className="text-muted">默认价</span>}
                </td>
                <td className="num">
                  {v.deposit != null
                    ? `¥${v.deposit}`
                    : <span className="text-muted">默认价</span>}
                </td>
                <td>
                  <select
                    className={`inline-select status-select-${statusMap[v.vehicleStatus]?.replace('status-', '') || 'default'}`}
                    value={v.vehicleStatus}
                    onChange={e => handleStatus(v, e.target.value)}
                  >
                    {STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
                  </select>
                </td>
                <td>
                  {/* 三个操作收进「…」菜单：单操作直显、多操作省略号（全站表格改造统一规则） */}
                  <RowActions items={[
                    { key: 'qr', label: '二维码', icon: 'qrcode', tone: 'default', onClick: () => setQrVehicle(v) },
                    { key: 'edit', label: '编辑', icon: 'replace', tone: 'default', onClick: () => handleEdit(v) },
                    { key: 'delete', label: '删除', icon: 'trash', tone: 'danger', onClick: () => handleDelete(v) },
                  ]} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* 分页：同类车再多也一页一页看；仅一页时不显示分页条 */}
      {!loading && total > 0 && (
        <Pagination
          total={total} page={page} pageSize={pageSize} onChange={setPage}
          onPageSizeChange={n => { setPageSize(n); setPage(1) }}
        />
      )}

      {loading && <div className="loading-line"><Icon name="refresh" size={18} />数据加载中…</div>}
      {!loading && total === 0 && (
        <div className="empty">
          <Icon name="empty" size={40} chip />
          <div>{list.length === 0 ? '暂无车辆数据' : '没有符合条件的车辆'}</div>
        </div>
      )}
      </>
      )}

      {showModal && (
        <div className="modal-overlay" onClick={closeAndReset}>
          <div className="modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <span>{editId ? '编辑车辆' : '新增车辆'}</span>
              <button className="close-btn" onClick={closeAndReset}><Icon name="close" size={15} /></button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label>车牌号 / 车辆编号 <span className="req">*</span></label>
                <input
                  className={errors.vehicleNo ? 'has-error' : ''}
                  value={form.vehicleNo}
                  onChange={e => setForm({ ...form, vehicleNo: e.target.value })}
                  placeholder="例如：桂A001"
                />
                <div className="field-hint">
                  车辆上路的法定牌照号，<strong>全店唯一</strong>：订单、小票、收款二维码都按它识别车辆，重号会导致账目张冠李戴。
                </div>
                {errors.vehicleNo && <div className="field-error">{errors.vehicleNo}</div>}
              </div>
              <div className="form-group">
                <label>品牌 <span className="req">*</span></label>
                <input
                  className={errors.brand ? 'has-error' : ''}
                  value={form.brand}
                  onChange={e => setForm({ ...form, brand: e.target.value })}
                  placeholder="例如：雅迪、爱玛"
                />
                <div className="field-hint">
                  品牌<strong>可以重复</strong>：同一品牌可以有很多辆车（如两台阿斯顿马丁），靠车牌号 / 车架号区分，无需唯一。
                </div>
                {errors.brand && <div className="field-error">{errors.brand}</div>}
              </div>
              <div className="form-group">
                <label>车架号</label>
                <input value={form.frameNumber} onChange={e => setForm({ ...form, frameNumber: e.target.value })} placeholder="可选" />
                <div className="field-hint">
                  厂家出厂编号（VIN），<strong>一车一号</strong>，是区分同品牌同型号不同车辆的最终依据；选填，填后不能与其它车辆重复。
                </div>
              </div>
              <div className="form-row">
                <div className="form-group">
                  <label>采购时间</label>
                  <input
                    type="date"
                    value={form.buyTime}
                    onChange={e => setForm({ ...form, buyTime: e.target.value })}
                  />
                </div>
                <div className="form-group">
                  <label>购入价（元）</label>
                  <input
                    type="number"
                    className={errors.buyPrice ? 'has-error' : ''}
                    value={form.buyPrice}
                    onChange={e => setForm({ ...form, buyPrice: e.target.value })}
                  />
                  {errors.buyPrice && <div className="field-error">{errors.buyPrice}</div>}
                </div>
              </div>
              <div className="form-row">
                <div className="form-group">
                  <label>日租金（元/天）</label>
                  <input
                    type="number"
                    className={errors.dailyRent ? 'has-error' : ''}
                    value={form.dailyRent}
                    onChange={e => setForm({ ...form, dailyRent: e.target.value })}
                    placeholder="留空用系统默认价"
                  />
                  {errors.dailyRent && <div className="field-error">{errors.dailyRent}</div>}
                </div>
                <div className="form-group">
                  <label>押金（元）</label>
                  <input
                    type="number"
                    className={errors.deposit ? 'has-error' : ''}
                    value={form.deposit}
                    onChange={e => setForm({ ...form, deposit: e.target.value })}
                    placeholder="留空用系统默认价"
                  />
                  {errors.deposit && <div className="field-error">{errors.deposit}</div>}
                </div>
              </div>
              <div className="field-hint">
                每辆车可以有自己的租金与押金，下单、结算、续租都按本车价格核算；留空则回退到「系统配置」里的默认价。
              </div>

              {/* 车辆图片：照片容器 + 容器下方的上传按钮行 */}
              <div className="form-group">
                <label>车辆图片</label>
                <div className="photo-box">
                  {shownImages.length > 0 ? (
                    <div className="picker-thumbs">
                      {shownImages.map(img => (
                        <div key={img.key} className={`picker-thumb ${img.primary ? 'is-primary' : ''}`}>
                          <img src={img.preview} alt="车辆图片" onClick={() => setLightbox(img.preview)} title="点击放大" />
                          {img.primary && <span className="picker-badge">主图</span>}
                          <div className="picker-ops">
                            <button className="picker-op" onClick={() => {
                              if (editId) { fileInputRef.current?.click(); return }
                              replaceKey.current = img.key
                              fileInputRef.current?.click()
                            }}>
                              <Icon name="replace" size={11} />替换
                            </button>
                            {!editId && (
                              <button className="picker-op" onClick={() => removePending(img.key)}>
                                <Icon name="close" size={11} />删除
                              </button>
                            )}
                          </div>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <div className="photo-box-empty">
                      <Icon name="camera" size={22} />
                      <span>暂无照片，点击下方按钮上传</span>
                    </div>
                  )}
                </div>
                <div className="img-picker-row">
                  <label className={`upload-trigger ${uploading ? 'is-loading' : ''}`}>
                    <Icon name={uploading ? 'refresh' : 'upload'} size={15} color="#fff" />
                    {uploading ? '上传中…' : editId && imageUrl ? '替换图片' : '选择图片'}
                    <input
                      ref={fileInputRef}
                      type="file"
                      accept="image/jpeg,image/png,image/webp,image/gif"
                      multiple={!editId}
                      disabled={uploading}
                      onChange={onFileInputChange}
                      style={{ display: 'none' }}
                    />
                  </label>
                  {/* 拍照上传：capture 直接调起后置摄像头（手机端才显示，桌面点它退化为文件选择器无意义故隐藏） */}
                  <label className="upload-trigger camera-trigger">
                    <Icon name="camera" size={15} color="#fff" />
                    拍照上传
                    <input
                      type="file"
                      accept="image/*"
                      capture="environment"
                      disabled={uploading}
                      onChange={onFileInputChange}
                      style={{ display: 'none' }}
                    />
                  </label>
                  {/* AI 生图：与上传 / 拍照并列的第三种图片来源（对接本机 ComfyUI 桌面版）。
                      入口是颗胶囊按钮，紧跟拍照上传；展开后整块面板独占一行（见 .img-picker-row > .ai-panel）。
                      传 form 进去，面板据此把「品牌」写进默认提示词——不用手写也能直接出图 */}
                  <AiImagePanel editId={editId} vehicle={form} onUse={handleAiUse} />
                  <span className="upload-hint">
                    支持 jpg / png / webp / gif，单张不超过 {MAX_IMG_MB}MB
                    {!editId && `，最多 ${MAX_IMAGES} 张，第一张为主图`}
                  </span>
                  {editId && imageUrl && (
                    <button className="btn-sm btn-sm-default" onClick={handleRemoveImage} disabled={uploading}>
                      <Icon name="close" size={13} />清除图片
                    </button>
                  )}
                </div>
                {imgError && <div className="field-error">{imgError}</div>}
                {!editId && pendingImages.length >= MAX_IMAGES && (
                  <div className="field-hint">已达 {MAX_IMAGES} 张上限，可先删除再添加。</div>
                )}
              </div>

              <div className="form-group">
                <label>车辆状态</label>
                <select value={form.vehicleStatus} onChange={e => setForm({ ...form, vehicleStatus: e.target.value })}>
                  {/* 编辑一辆「在租」车时把「在租」放回选项，否则下拉会显示空白；
                      此时改成其它状态会被后端拦下并提示先归还结算，正是期望行为。 */}
                  {(form.vehicleStatus === '在租' ? STATUSES : SELECTABLE_STATUSES)
                    .map(s => <option key={s} value={s}>{s}</option>)}
                </select>
                {/* 「在租」由开单自动占用产生，人工选出来会造出一辆租不出去也结不了算的车 */}
                <div className="field-hint">「在租」由开单流程自动生成，不可手工选择；如需出租请在「租赁订单 → 创建订单」中开单。</div>
                {/* 用况4 扩展用况3：编辑态改动状态同样需管理员密码（保存时校验） */}
                {editId && (
                  <div className="field-hint">更改车辆状态需二次输入管理员登录密码，保存时会弹出校验。</div>
                )}
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn-sm btn-sm-default" onClick={closeAndReset}>取消</button>
              <button className="btn-sm btn-sm-primary" onClick={handleSave}>保存</button>
            </div>
          </div>
        </div>
      )}

      {/* 车辆专属二维码：查看 / 打印 / 下载（用况4、需求 4.2.2） */}
      {qrVehicle && <VehicleQrModal vehicle={qrVehicle} onClose={() => setQrVehicle(null)} />}

      {/* 扫码查询：扫码枪直接扫，或手工输入车辆编号 */}
      {showLookup && <VehicleQrLookup onClose={() => setShowLookup(false)} />}

      {/* 图片放大预览：列表缩略图与弹窗内预览共用，交互全站一致 */}
      {lightbox && <ImageLightbox src={lightbox} alt="车辆图片" onClose={() => setLightbox(null)} />}

      {/* 批量新增 / 批量编辑：勾选多辆车后操作，与单条新增/编辑并列 */}
      {showBatchCreate && (
        <BatchCreateModal
          onClose={() => setShowBatchCreate(false)}
          onDone={load}
        />
      )}
      {showBatchEdit && (
        <BatchEditModal
          ids={selected}
          onClose={() => setShowBatchEdit(false)}
          onDone={() => { setSelected([]); load() }}
        />
      )}
    </div>
  )
}
