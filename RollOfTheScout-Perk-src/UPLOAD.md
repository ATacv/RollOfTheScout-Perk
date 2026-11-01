# 上传到 GitHub 的步骤（不用给 token）

本目录已打好包：**`RollOfTheScout-Perk-src.zip`**（1.13 MB，34 个条目，已排除 `bin/`、`obj/`、`*.zip`、`Config.Build.user.props`）。

---

## 方案 A：GitHub Desktop（最省事，推荐给不熟命令行）

1. 装 [GitHub Desktop](https://desktop.github.com/) 并登录你的账号
2. 网页新建仓库：https://github.com/new → 名字如 `RollOfTheScout-Perk` → **不要**勾 README / .gitignore / license
3. Desktop 里 `File → Clone repository` → 选中刚建的空仓库 → 选一个本地路径（例如 `D:\dev\RollOfTheScout-Perk`）
4. 把 **zip 解压**后的所有内容**拷进**上一步的目录（覆盖空目录）
5. Desktop 里会自动列出改动 → 左下角填 Summary：`词条 + 怪谈改造版：词条系统 / 三池抽取 / 显示专用版`
6. 点 **Commit to main** → 再点右上 **Publish branch / Push origin**

---

## 方案 B：命令行（git）

```powershell
# 1) 装 git（winget 已在）
winget install --id Git.Git -e --source winget

# 2) 新开一个终端（让 PATH 生效），然后
git --version

# 3) 解压 zip 到某个目录，例如 D:\dev\RollOfTheScout-Perk，然后进入
cd D:\dev\RollOfTheScout-Perk

# 4) 初始化并提交
git init -b main
git config user.name  "你的名字"
git config user.email "你的邮箱"
git add -A
git commit -m "词条 + 怪谈改造版：词条系统 / 三池抽取 / 显示专用版"

# 5) 关联远端（先按网页提示建好空仓库）
git remote add origin https://github.com/你的用户名/RollOfTheScout-Perk.git

# 6) 推送（会弹浏览器/输入账号，用用户名 + Token 或登录窗口即可）
git push -u origin main
```

---

## 上传前注意

| 项 | 说明 |
|---|---|
| **LICENSE** | 已在包内（GPLv3）。这是衍生作品，必须保留原版权与许可 |
| **署名** | `README.md` 末尾已加「基于 Rhys-214/RollOfTheScout 修改，GPLv3」段 |
| **不该上传的文件** | `Config.Build.user.props`（含本机绝对路径）已被 `.gitignore` 排除；`bin/`、`obj/` 同样排除 |
| **dll 要不要传** | `dist/` 里两版 dll 在包内。不想让仓库含二进制就删掉 `dist/` 再传，改用 Release 附件 |
| **cfg** | `*.cfg` 已排除（运行期生成，含本机配置） |

---

## 推送后建议

1. 在仓库 **Settings → About** 里补一句描述与 Topics（`peak`、`bepinex`、`mod`、`creepypasta`）
2. 想发 dll 的话：**Releases → Draft a new release**，tag 填 `v1.0.7`，把 `dist/` 里两个 dll 作为附件上传
3. 首次推送完成后，本地目录就已经是 git 仓库了，之后我改完代码你可以直接 `git add -A && git commit && git push`
