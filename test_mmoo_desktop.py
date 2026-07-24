import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from bs4 import BeautifulSoup
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=1280,1024')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(5)

# Try to find and close popup
try:
    close_btns = driver.find_elements(webdriver.common.by.By.XPATH, "//*[contains(text(), 'Đóng') or @translate='Common_Closed' or contains(@ng-click, 'ok')] | //*[contains(@class, 'close')]")
    for b in close_btns:
        driver.execute_script('arguments[0].click();', b)
    time.sleep(2)
except Exception as e:
    pass

soup = BeautifulSoup(driver.page_source, 'html.parser')
buttons = soup.find_all(['button', 'a', 'div'])
for b in buttons:
    try:
        t = b.text.strip().replace('\n', ' ')
        if 'ĐĂNG KÝ' in t.upper() or 'REGISTER' in t.upper() or 'ĐĂNG NHẬP' in t.upper():
            if len(t) < 30:
                print('Found:', b.name, b.attrs, t)
    except:
        pass

driver.quit()
