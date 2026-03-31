# Gallery Ship 로컬 배포 지침

이 문서는 로컬 배포용 메모입니다.

- GitHub 저장소에는 올리지 않습니다.
- 기준 작업 폴더:
  - 소스: `D:\SteamLibrary\steamapps\common\Slay the Spire 2\_mod_sources\GalleryShip`
  - 퍼블리시 클론: `C:\Users\kimsg\AppData\Local\Temp\STS2-Gallery-Ship-publish`

## 1. 버전 올리기

아래 파일의 버전을 같은 값으로 맞춥니다.

- `mod_manifest.json`
- `Properties/AssemblyInfo.cs`
- `README.md` 다운로드 링크

## 2. 로컬 배포

게임이 꺼진 상태에서 소스 폴더에서 아래 명령을 실행합니다.

```powershell
.\publish.ps1
```

배포 대상:

- `D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\galleryship`
- `D:\SteamLibrary\steamapps\common\Slay the Spire 2\_publish\galleryship_build_ready`

## 3. 릴리즈 ZIP 만들기

스테이징 폴더를 기준으로 `GalleryShip-x.y.z.zip` 파일을 만듭니다.

예시 출력:

- `D:\SteamLibrary\steamapps\common\Slay the Spire 2\_publish\GalleryShip-1.1.1.zip`

ZIP 안에는 아래 3개 파일이 들어가야 합니다.

- `galleryship.dll`
- `mod_manifest.json`
- `gallery_ship_button.png`

## 4. GitHub 반영

퍼블리시 클론에 아래 항목만 동기화합니다.

- `GalleryShip/`
- `Properties/`
- `docs/`
- `GalleryShip.csproj`
- `README.md`
- `LICENSE`
- `.gitignore`
- `mod_manifest.json`
- `publish.ps1`
- `gallery_ship_button.png`

주의:

- `LOCAL_RELEASE_GUIDE.md` 는 퍼블리시 클론으로 복사하지 않습니다.
- `bin/`, `obj/`, 로컬 캐시 파일은 올리지 않습니다.

## 5. GitHub 릴리즈

릴리즈 절차:

1. 퍼블리시 클론에서 커밋/푸시
2. `vx.y.z` 태그 기준 릴리즈 생성
3. `GalleryShip-x.y.z.zip` 업로드
4. 릴리즈 본문은 UTF-8 한글로 작성

권장 릴리즈 본문 형식:

```text
Gallery Ship x.y.z 배포판입니다.

- 자동 업데이트 반복 안내 버그 수정
- 갤망호 탐색 안정성 조정
- 최신 배포 파일과 내부 버전 반영
```

## 6. 최종 확인

- 게임에서 모드가 정상 로드되는지 확인
- 메인 화면 자동 업데이트 문구가 중복으로 뜨지 않는지 확인
- GitHub 릴리즈 페이지에서 한글이 깨지지 않는지 확인
