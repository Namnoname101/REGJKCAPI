import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=1280,1024')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(5)

# Try to close popup
close_btns = driver.find_elements(By.XPATH, "//*[@translate='Common_Closed' or contains(@ng-click, 'ok')] | //div[contains(text(), 'Đóng')] | //button[contains(text(), 'Đóng')]")
for b in close_btns:
    try: driver.execute_script('arguments[0].click();', b)
    except: pass
time.sleep(2)

# Try to find register button
btns = driver.find_elements(By.XPATH, "//button[contains(text(), 'ĐĂNG KÝ') or contains(@translate, 'Register') or contains(@ng-click, 'openRegister')] | //a[contains(text(), 'ĐĂNG KÝ')]")
clicked = False
for b in btns:
    if 'trải nghiệm' not in b.text:
        print('Clicking:', b.tag_name, b.get_attribute('class'))
        driver.execute_script('arguments[0].click();', b)
        clicked = True
        break

if clicked:
    try:
        WebDriverWait(driver, 5).until(EC.presence_of_element_located((By.CSS_SELECTOR, "input[ng-model='.user.account.value']")))
        print("Form opened successfully!")
    except Exception as e:
        print("Form failed to open!", e)

driver.quit()
