import requests
import os
import time
from bs4 import BeautifulSoup

# 디시 갤러리 ID (슬갤: slay)
GALLERY_ID = "slay"
SAVE_DIR = "captchas_raw"

if not os.path.exists(SAVE_DIR):
    os.makedirs(SAVE_DIR)

session = requests.Session()
session.headers.update({
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36"
})

def download_captcha(index):
    try:
        # 1. 글쓰기 페이지 방문하여 캡챠 ID 획득
        write_url = f"https://gall.dcinside.com/mgallery/board/write/?id={GALLERY_ID}"
        resp = session.get(write_url)
        soup = BeautifulSoup(resp.text, 'html.parser')
        
        # 2. 캡챠 이미지 주소 찾기 (보통 id="captcha_img" 또는 특정 패턴)
        # 이미지 주소 예: https://captcha.dcinside.com/index.php?game_id=xxx
        img_tag = soup.find('img', {'id': 'captcha_img'})
        if not img_tag:
            print(f"[{index}] 캡챠 이미지를 찾을 수 없습니다. (reCAPTCHA v2 활성화 여부 확인 필요)")
            return False
            
        img_url = img_tag['src']
        if img_url.startswith("//"):
            img_url = "https:" + img_url
            
        # 3. 이미지 저장
        img_resp = session.get(img_url)
        with open(f"{SAVE_DIR}/captcha_{index}.png", "wb") as f:
            f.write(img_resp.content)
            
        print(f"[{index}] 캡챠 수집 완료: {SAVE_DIR}/captcha_{index}.png")
        return True
    except Exception as e:
        print(f"[{index}] 에러 발생: {e}")
        return False

# 100장 수집 시작
count = 0
while count < 100:
    if download_captcha(count):
        count += 1
    time.sleep(1) # 디시 서버 부하 방지 및 차단 회피용 1초 대기

print(f"\n총 {count}장의 캡챠 수집을 완료했습니다! 이제 폴더에서 파일 이름을 정답으로 바꿔주세요.")
