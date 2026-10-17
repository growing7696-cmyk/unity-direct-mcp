# Unity Direct MCP

**유니티 에디터가 곧 MCP 서버예요.** 패키지 하나 설치하고 버튼 한 번 누르면 Claude Code, Cursor, VS Code가 게임을 보고, 만들고, 직접 플레이해서 확인해요.

- **Python, Node.js, 계정 필요 없음.** 서버가 에디터 안에서 `127.0.0.1`로 돌아가요.
- **도구는 8개.** 그중 하나가 에디터 API 전체로 C#을 실행할 수 있어서, 필요한 도구가 없어서 막히는 일이 없어요.
- **AI가 스스로 검증.** 컴파일 에러, 콘솔 로그, 플레이 모드, 스크린샷으로 "작성 → 컴파일 → 수정 → 플레이 → 확인" 루프를 혼자 돌려요.
- **스크립트 리로드에도 안 끊김.** 처리 중이던 요청은 연결이 끊기는 대신 "리로드 중" 응답을 받고, 서버는 약 2초 안에 다시 떠요.

## 설치 (2분)

1. 유니티 → **Window → Package Manager → + → Add package from git URL**
   ```
   https://github.com/growing7696-cmyk/unity-direct-mcp.git?path=/Package
   ```
2. **Direct MCP** 창이 열려요. 초록불이면 서버가 켜진 거예요.
3. 쓰는 AI 도구 옆의 **Connect**를 누르면 프로젝트에 설정 파일이 생겨요.
   - Claude Code → `.mcp.json`
   - Cursor → `.cursor/mcp.json`
   - VS Code (Copilot) → `.vscode/mcp.json`
4. 프로젝트 폴더에서 AI 도구를 열고 요청하세요.
   > "WASD로 움직이는 플레이어 캡슐을 만들고, 앞에 상자 5개를 놓은 다음, 플레이해서 스크린샷 보여줘."

Unity 2022.3 이상이 필요해요. Unity 6000.6(macOS)에서 테스트했어요.

## 도구

| 도구 | 하는 일 |
|---|---|
| `unity_status` | 씬, 플레이 모드, 컴파일 상태, **마지막 컴파일 에러**, 콘솔 에러 수 |
| `scene_hierarchy` | 오브젝트 트리와 컴포넌트 목록 |
| `inspect_object` | 컴포넌트의 모든 필드 값 (에셋도 가능) |
| `execute_csharp` | 에디터에서 C# 실행 (컴파일 약 0.3초). 결과와 로그 반환, 실행 취소(Undo) 한 번으로 되돌리기 가능 |
| `refresh_assets` | AI가 쓴 파일을 가져오고 컴파일 시작 |
| `play_mode` | 플레이 시작, 종료, 일시정지, 재개 |
| `read_console` | 콘솔 로그와 스택 트레이스 (리로드 후에도 유지) |
| `screenshot` | 게임/씬 카메라 화면을 이미지로 반환 |

## 보안

서버는 `127.0.0.1`에서만 열리고, 외부 웹페이지에서 오는 요청은 거부해요. `execute_csharp`는 에디터가 할 수 있는 모든 걸 할 수 있으니 믿을 수 있는 AI 도구만 연결하세요. 창에서 언제든 서버를 끌 수 있어요.

## 라이선스

MIT. C# 코드 실행 방식은 [MCP for Unity](https://github.com/CoplayDev/unity-mcp)(MIT)를 참고했어요.
