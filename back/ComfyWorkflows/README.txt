AI 生图工作流目录（ComfyWorkflows）使用说明
==============================================

本目录存放「ComfyUI 工作流模板」。系统（车辆档案 → 车辆图片 → AI 生成）
选中模板后，会按模板里的实际节点 + ComfyUI 实时的 object_info 定义
「动态生成表单」：模板里有哪些可填的输入，页面上就出现哪些控件——
模型、LoRA、步数、CFG、宽高、多区提示词、shift、batch_size……
与节点 id 无关，也不挑工作流（文生图 / 图生图 / 带 ControlNet 的都能用）。

文件名（不含 .json）就是页面上的模板名，可随意改名、可增删。


一、怎么把桌面版的工作流放进来
--------------------------------
1. 打开 Comfy Desktop，加载/调好你的工作流；
2. 右上角菜单 →「Save」→「Save (API Format)」（务必选 API Format，
   普通格式带 UI 坐标信息，程序无法提交）；
3. 把导出的 .json 文件复制到本目录（back/ComfyWorkflows/）；
4. 在页面「AI 生成」面板里选中它，表单即刻按这个工作流重新生成。


二、目录里现成的模板
--------------------------------
  Z-Image-Turbo_文生图.json   已按本机模型实况配好（开箱即用）：
                            UNET = z_image_turbo_int8_convrot.safetensors
                            CLIP = qwen_3_4b_fp8_mixed.safetensors（lumina2）
                            VAE  = ae.safetensors
                            8 步 / CFG 1 / res_multistep+simple / 1024×1024
  Z-Image-Turbo_图生图.json   同上，参考图先 ImageScale 到目标尺寸再进
                            VAEEncode，重绘幅度默认 0.6
  文生图_SD15示例.json        标准 SD1.5 文生图（需要 models/checkpoints/
                            里有 .safetensors 主模型才能跑）
  图生图_SD15示例.json        标准 SD1.5 图生图（同上，重绘幅度 0.75）

注意：Z-Image 两份模板里的模型文件名必须与 ComfyUI 模型目录里的实际
文件名一致；换了量化版本（如 bf16 → int8）后，要么改模板里的文件名，
要么用页面上的模型下拉重新选。


三、表单是怎么生成的（schema 驱动）
--------------------------------
选中模板后，后端读取模板每个节点，对照 ComfyUI object_info 的输入定义，
按输入名归类成几组渲染（与节点 id 无关）：

  提示词      CLIPTextEncode 的 text（多行文本框；模板里有几个文本节点
             就有几个提示词框，区域提示词也能分别填）
  模型与LoRA  ckpt_name / unet_name / vae_name / clip_name / lora_name /
             weight_dtype / device / type 等下拉（选项 = 磁盘实况文件清单）
  尺寸        width / height（附常用尺寸预设一键快填，自动取 8 的倍数）
  采样参数    seed（-1 = 每次随机）/ steps / cfg / denoise / sampler_name /
             scheduler / shift
  图片        LoadImage 的 image（图生图由上传的参考图自动覆写）
  输出        filename_prefix
  其他参数    其余可填输入（如 batch_size）

只有「提示词」必填；其余全部留空 = 用模板原值。连接关系、采样器、
放大节点、保存节点一律不动——你的图怎么连，生成就怎么跑。


四、常见问题
--------------------------------
Q1 面板提示「ComfyUI 未启动或地址不通」
   → 先打开 Comfy Desktop 并等界面完全加载（它自带 127.0.0.1:8188 服务）。
     地址换了机器/端口（桌面版改了端口）后，到「系统配置」页顶部的
     「ComfyUI 设置（AI 生图）」卡片里改：填新地址 → 先点「测试连接」
     验通 → 再「保存」，之后全站生效（存在数据库里，重启不丢；
     没配过时才回退到 back/appsettings.json 的 AiImage.BaseUrl）。
     面板打开时也会显示当前生效地址与来源。

Q2 提交报错「工作流校验未通过：……取值不存在 / 可选列表为空」
   → 提交前的预检（对照 ComfyUI 实时的 object_info）：模板引用的模型文件
     在 models 对应子目录里不存在，或目录里根本没有文件。处理：把模型文件
     放进 ComfyUI 的 models 子目录（桌面版默认 ComfyUI-Shared/models/）后
     重启 ComfyUI；或在页面模型下拉里改选一个已存在的模型。
     4GB 显存（RTX 3050 Laptop）建议 SD1.5 / GGUF 量化 / Z-Image-Turbo
     int8 这类轻量模型，SDXL / Flux bf16 容易显存不足。
     若报错指向 LoadImage 的 image：说明文生图模式选到了图生图模板——
     切到「图生图」页签并上传参考图（面板会自动换上图生图模板），
     或换用文生图模板。

Q2b 页面模板下拉与模式的关系
   → 模板名含「文生图 / text2img / t2i」归文生图模式，含「图生图 /
     img2img / i2i」归图生图模式；打开面板、切换页签时都会自动选中
     与模式匹配的模板，选项里也带（文生图）/（图生图）标记。
     自己加的模板不想被自动切换，起个不带这些字样的名字即可。

Q3 表单里某个下拉是空的
   → ComfyUI 离线时先按模板默认值填写（启动后刷新即出现选项）；
     或对应模型目录里还没有文件（同 Q2）。

Q4 提交报错「有未注册的节点类型」
   → 模板用了 ComfyUI 自定义节点（如 MiniMax 相关节点）而本机没装。
     在桌面版 ComfyUI Manager 里安装对应插件、重启 ComfyUI 后再试。

Q5 提交报错「模板不是 API 格式工作流」
   → 导出时没选 Save (API Format)。按第一节重新导出。

Q6 生成很慢 / 超时
   → 单张超时默认 600 秒（appsettings.json 的 AiImage.TimeoutSeconds）。
     慢就降尺寸、降步数，或在模板里换更轻的模型。

Q7 生成的图存在哪里
   → 服务端落盘到 back/wwwroot/uploads/ai/，页面上「使用该图」
     即把它设为当前车辆的主图（车辆档案里也能直接上传普通图片）。


五、文件清单
--------------------------------
  Z-Image-Turbo_文生图.json   Z-Image-Turbo 文生图（本机模型实况，开箱即用）
  Z-Image-Turbo_图生图.json   Z-Image-Turbo 图生图（ImageScale + VAEEncode）
  文生图_SD15示例.json        标准 SD1.5 文生图（需自备主模型）
  图生图_SD15示例.json        标准 SD1.5 图生图（需自备主模型）
  你后续把自己的工作流 json 放进本目录即可。
