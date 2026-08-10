# 安装官方 Ponytail 插件(Claude Code)

在**交互式 Claude Code 终端**(非本会话)中,把下面两条命令**分别**作为独立提示发送。

### 第 1 步:添加市场

```
/plugin marketplace add DietrichGebert/ponytail
```

### 第 2 步:安装插件

```
/plugin install ponytail@ponytail
```

说明:
- 两条命令必须分两次发送(官方 README 要求)。
- 该插件通过生命周期钩子每轮注入 Ponytail 规则,并提供 `/ponytail [lite|full|ultra|off]`、`/ponytail-review`、`/ponytail-audit` 等命令。
- 要求 `node` 在 PATH 上;若不在,skill 仍可用,始终在线激活会静默失效。
- 安装后重启会话即可生效。
