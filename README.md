![Gallery Ship 미리보기](docs/galleryship-preview.webp)

[다운로드](https://github.com/chocotea1008/STS2-Gallery-Ship/releases/tag/v1.1.2)

# STS2 Gallery Ship

`Slay the Spire 2` 멀티플레이 메뉴에 `갤망호` 카드를 추가하는 모드입니다.

갤망호 화면에서는 디시인사이드 슬레이 더 스파이어 갤러리의 슬망호 글을 읽고, 게시글 안의 `steam://joinlobby/...` 링크를 기준으로 실제 참여 가능한 방만 추려서 보여줍니다.

## 기능

- 멀티플레이 메뉴에 `갤망호` 카드 추가
- 슬레이 더 스파이어 갤러리 슬망호 글 목록 크롤링
- `Steam JoinLobby` 와 초기 handshake 기준으로 참가 가능한 방만 필터링
- 접속 가능한 방의 제목, 인원, 직업 아이콘, 닉네임 표시
- `꽉 참`, `이미 시작`, `연결 실패`로 판정된 글은 이후 새로고침에서 재크롤링을 최소화
- 일반 `호스트`로 방을 만들면 전체 `steam://joinlobby/...` 링크를 클립보드에 복사
- GitHub 릴리즈 기반 자동 업데이트 지원
- 이 모드는 갤망호를 여는 `호스트`가 설치하지 않아도 작동합니다.

## 설치 방법

1. 릴리즈 페이지에서 ZIP 파일을 받습니다.
2. 압축을 풀면 `galleryship` 폴더가 나옵니다.
3. 그 폴더를 `Slay the Spire 2/mods/` 아래에 넣습니다.
4. 게임을 실행한 뒤 멀티플레이 메뉴에서 `갤망호` 카드를 확인합니다.

## 사용 방법

- `갤망호` 카드를 누르면 참가 가능한 방 목록을 불러옵니다.
- 목록의 방을 누르면 해당 글의 `steam://joinlobby/...` 링크로 접속합니다.
- 일반 `호스트`로 방을 만든 뒤에는 참가 링크가 자동으로 클립보드에 복사됩니다.

## 구성 파일

- `GalleryShip/`: 모드 C# 소스
- `mod_manifest.json`: STS2 모드 메타데이터
- `gallery_ship_button.png`: 갤망호 카드 이미지
- `publish.ps1`: 로컬 배포 스크립트

## 빌드

프로젝트 루트에서 아래 명령으로 Release 빌드를 만들 수 있습니다.

```powershell
dotnet build .\GalleryShip.csproj -c Release
```

빌드된 DLL은 `bin/Release/netcoreapp9.0/galleryship.dll` 에 생성됩니다.

## 주의 사항

- `affects_gameplay` 는 `false` 로 설정되어 있습니다.
- 참가 가능 여부는 Steam 로비 probe 결과를 기준으로 판정합니다.
- 디시 글 구조나 Steam 응답 형식이 바뀌면 후속 수정이 필요할 수 있습니다.

## 라이선스

이 프로젝트는 [MIT License](LICENSE)를 따릅니다.
