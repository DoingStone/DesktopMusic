$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# A spread across eras, genres and popularity, to stand in for a daily-recommendation feed
# rather than a hand-picked list of songs known to be well covered.
$songs = @(
  '呓语 毛不易'
  '我的天空 南征北战'
  '起风了 买辣椒也用券'
  '漠河舞厅 柳爽'
  '罗刹海市 刀郎'
  '孤勇者 陈奕迅'
  '晴天 周杰伦'
  '夜曲 周杰伦'
  '稻香 周杰伦'
  '后来 刘若英'
  '十年 陈奕迅'
  '富士山下 陈奕迅'
  '海阔天空 Beyond'
  '光辉岁月 Beyond'
  '装糊涂 许嵩'
  '有何不可 许嵩'
  '错位时空 艾辰'
  '白月光与朱砂痣 大籽'
  '云与海 阿YueYue'
  '探窗 浮生梦'
  '悬溺 葛东琪'
  '乌梅子酱 李荣浩'
  '小镇姑娘 陶喆'
  '爱在西元前 周杰伦'
) -join ';'

& "src\TaskbarLyrics.Cli\bin\Release\net8.0-windows10.0.19041.0\tblc.exe" coverage $songs 2>&1 |
  ForEach-Object { "  $_" }
