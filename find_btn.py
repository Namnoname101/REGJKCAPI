import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=375,812')
driver = webdriver.Chrome(options=options)
driver.get('https://www.88jin88.com/m/register?r=tbk8366')

try:
    WebDriverWait(driver, 15).until(EC.presence_of_element_located((By.TAG_NAME, 'input')))
except Exception as e:
    print('Wait failed:', e)

buttons = driver.find_elements(By.CSS_SELECTOR, "button, a, div[class*='btn'], div[class*='register']")
for b in buttons:
    try:
        t = b.text.strip().replace('\n', ' ')
        if 'Đăng ký' in t:
            print(f'Found: tag: {b.tag_name}, class: {b.get_attribute("class")}, id: {b.get_attribute("id")}')
    except:
        pass
driver.quit()
